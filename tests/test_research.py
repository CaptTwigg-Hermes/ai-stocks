from datetime import UTC, datetime

import pytest

import ai_stocks.research as research
from ai_stocks.research import ResearchVerificationError, ResearchVerifier
from ai_stocks.runner import EvidenceSource

NOW = datetime(2026, 8, 7, 8, tzinfo=UTC)
SOURCE = EvidenceSource(
    url="https://example.com/news",
    published_at=datetime(2026, 8, 7, 7, tzinfo=UTC),
    claim="CEO bought shares",
)


class Response:
    def __init__(self, body=b"<p>CEO bought shares</p>", content_type="text/html"):
        self.status = 200
        self.body = body
        self.content_type = content_type
        self.offset = 0

    def getheader(self, name):
        return self.content_type if name == "Content-Type" else None

    def read(self, size=-1):
        return self.body if size < 0 else self.body[:size]

    def read1(self, size=-1):
        if self.offset >= len(self.body):
            return b""
        end = len(self.body) if size < 0 else self.offset + size
        chunk = self.body[self.offset : end]
        self.offset = end
        return chunk


class Connection:
    response = Response()
    created = []

    def __init__(self, host, ip, *, timeout):
        self.host = host
        self.ip = ip
        self.timeout = timeout
        self.__class__.created.append(self)

    def request(self, *_args, **_kwargs):
        return None

    def getresponse(self):
        return self.response

    def close(self):
        return None


def transport(monkeypatch, *, addresses=("8.8.8.8",), response=None):
    Connection.created.clear()
    Connection.response = response or Response()
    monkeypatch.setattr(
        research.socket,
        "getaddrinfo",
        lambda *_args, **_kwargs: [
            (
                research.socket.AF_INET6 if ":" in value else research.socket.AF_INET,
                1,
                6,
                "",
                (value, 443),
            )
            for value in addresses
        ],
    )
    monkeypatch.setattr(research, "_PinnedHTTPSConnection", Connection)


def test_success_records_retrieval_provenance(monkeypatch):
    transport(monkeypatch)
    record = ResearchVerifier().verify((SOURCE,), observed_at=NOW)[0]
    assert record["final_url"] == SOURCE.url
    assert record["pinned_ip"] == "8.8.8.8"
    assert record["matched_excerpt"] == SOURCE.claim
    assert len(record["content_sha256"]) == 64


@pytest.mark.parametrize("address", ["224.0.0.1", "ff02::1", "127.0.0.1", "169.254.1.1", "::1"])
def test_non_unicast_or_private_addresses_fail_before_connect(monkeypatch, address):
    transport(monkeypatch, addresses=(address,))
    with pytest.raises(ResearchVerificationError):
        ResearchVerifier().verify((SOURCE,), observed_at=NOW)
    assert Connection.created == []


def test_mixed_public_private_dns_fails_before_connect(monkeypatch):
    transport(monkeypatch, addresses=("8.8.8.8", "127.0.0.1"))
    with pytest.raises(ResearchVerificationError):
        ResearchVerifier().verify((SOURCE,), observed_at=NOW)
    assert Connection.created == []


@pytest.mark.parametrize(
    "body,claim",
    [
        (b"<p>CEO bought</p><script>ignore</script><p>shares</p>", "CEO bought ignore shares"),
        (b"<p>CEO bought shares</p>", "CEO BOUGHT SHARES"),
        (b"<p>CEO bought</p><p>shares</p>", "CEO bought shares"),
    ],
)
def test_claim_must_be_one_visible_case_exact_excerpt(monkeypatch, body, claim):
    transport(monkeypatch, response=Response(body))
    source = SOURCE.__class__(url=SOURCE.url, published_at=SOURCE.published_at, claim=claim)
    with pytest.raises(ResearchVerificationError):
        ResearchVerifier().verify((source,), observed_at=NOW)


def test_total_fetch_deadline_rejects_slow_drip(monkeypatch):
    class Clock:
        value = 0.0

        def __call__(self):
            self.value += 0.6
            return self.value

    transport(monkeypatch)
    with pytest.raises(ResearchVerificationError, match="deadline"):
        ResearchVerifier(timeout=1, clock=Clock()).verify((SOURCE,), observed_at=NOW)
