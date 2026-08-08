"""Pinned-IP, independently verified public research evidence."""

from __future__ import annotations

import hashlib
import html
import http.client
import ipaddress
import re
import socket
import ssl
from collections.abc import Callable
from datetime import datetime
from html.parser import HTMLParser
from time import monotonic
from urllib.parse import urljoin, urlsplit

from .runner import EvidenceSource

_MAX_BODY = 1_048_576
_MAX_REDIRECTS = 3
_SPACE = re.compile(r"\s+")
_BLOCK_TAGS = frozenset(
    {
        "address",
        "article",
        "aside",
        "blockquote",
        "br",
        "div",
        "footer",
        "h1",
        "h2",
        "h3",
        "h4",
        "h5",
        "h6",
        "header",
        "hr",
        "li",
        "main",
        "nav",
        "ol",
        "p",
        "pre",
        "section",
        "table",
        "td",
        "th",
        "tr",
        "ul",
    }
)
_HIDDEN_TAGS = frozenset({"script", "style", "noscript", "template"})


class ResearchVerificationError(RuntimeError):
    pass


class _VisibleText(HTMLParser):
    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self._hidden_depth = 0
        self._parts: list[str] = []

    def handle_starttag(self, tag: str, attrs) -> None:
        del attrs
        if tag in _HIDDEN_TAGS:
            self._hidden_depth += 1
        elif self._hidden_depth == 0 and tag in _BLOCK_TAGS:
            self._parts.append("\n")

    def handle_endtag(self, tag: str) -> None:
        if tag in _HIDDEN_TAGS:
            self._hidden_depth = max(0, self._hidden_depth - 1)
        elif self._hidden_depth == 0 and tag in _BLOCK_TAGS:
            self._parts.append("\n")

    def handle_data(self, data: str) -> None:
        if self._hidden_depth == 0:
            self._parts.append(data)

    def lines(self) -> tuple[str, ...]:
        return tuple(filter(None, (_normalize(part) for part in "".join(self._parts).splitlines())))


class _PinnedHTTPSConnection(http.client.HTTPSConnection):
    def __init__(self, host: str, ip: str, *, timeout: float) -> None:
        context = ssl.create_default_context()
        super().__init__(host, 443, timeout=timeout, context=context)
        self._pinned_ip = ip
        self._tls_context = context

    def connect(self) -> None:
        raw = socket.create_connection((self._pinned_ip, 443), self.timeout)
        self.sock = self._tls_context.wrap_socket(raw, server_hostname=self.host)


class ResearchVerifier:
    def __init__(self, *, timeout: float = 15, clock: Callable[[], float] = monotonic) -> None:
        self.timeout = timeout
        self.clock = clock

    def verify(
        self, sources: tuple[EvidenceSource, ...], *, observed_at: datetime
    ) -> tuple[dict[str, object], ...]:
        if observed_at.tzinfo is None or observed_at.utcoffset() is None:
            raise ResearchVerificationError("research observation time must be timezone-aware")
        return tuple(self._verify_one(source, observed_at) for source in sources)

    def _verify_one(self, source: EvidenceSource, observed_at: datetime) -> dict[str, object]:
        body, final_url, pinned_ip = self._fetch(source.url)
        claim = _normalize(source.claim)
        parser = _VisibleText()
        parser.feed(html.unescape(body.decode("utf-8", "strict")))
        if not claim or not any(claim in line for line in parser.lines()):
            raise ResearchVerificationError(
                "cited claim is not a verbatim excerpt of the fetched source"
            )
        return {
            "verified": True,
            "verification_mode": "independent_fetch",
            "url": source.url,
            "final_url": final_url,
            "published_at": source.published_at.isoformat(),
            "observed_at": observed_at.isoformat(),
            "claim": source.claim,
            "matched_excerpt": claim,
            "pinned_ip": pinned_ip,
            "content_sha256": hashlib.sha256(body).hexdigest(),
        }

    def _fetch(self, url: str) -> tuple[bytes, str, str]:
        current = url
        deadline = self.clock() + self.timeout
        for _ in range(_MAX_REDIRECTS + 1):
            remaining = self._remaining(deadline)
            parsed = urlsplit(current)
            if parsed.scheme != "https" or not parsed.hostname or parsed.port not in (None, 443):
                raise ResearchVerificationError("research URL must be public HTTPS on port 443")
            if parsed.username or parsed.password or parsed.fragment:
                raise ResearchVerificationError("research URL authority or fragment is invalid")
            host = parsed.hostname.encode("idna").decode("ascii")
            addresses = {
                str(item[4][0]) for item in socket.getaddrinfo(host, 443, type=socket.SOCK_STREAM)
            }
            remaining = self._remaining(deadline)
            parsed_addresses = tuple(ipaddress.ip_address(value) for value in addresses)
            if not parsed_addresses or any(
                not address.is_global or address.is_multicast or address.is_unspecified
                for address in parsed_addresses
            ):
                raise ResearchVerificationError(
                    "research hostname does not resolve exclusively public"
                )
            pinned_ip = sorted(addresses)[0]
            connection = _PinnedHTTPSConnection(host, pinned_ip, timeout=remaining)
            path = parsed.path or "/"
            if parsed.query:
                path += "?" + parsed.query
            try:
                connection.request(
                    "GET",
                    path,
                    headers={
                        "Accept": "text/html,application/xhtml+xml,application/json,text/plain,application/xml",
                        "Accept-Encoding": "identity",
                        "User-Agent": "ai-stocks/0.1 research verifier",
                    },
                )
                self._remaining(deadline)
                response = connection.getresponse()
                self._remaining(deadline)
                if response.status in {301, 302, 303, 307, 308}:
                    location = response.getheader("Location")
                    if not location:
                        raise ResearchVerificationError("research redirect has no location")
                    current = urljoin(current, location)
                    continue
                if response.status != 200:
                    raise ResearchVerificationError(
                        f"research source returned HTTP {response.status}"
                    )
                content_type = (response.getheader("Content-Type") or "").lower()
                if not any(
                    item in content_type
                    for item in ("text/", "application/json", "application/xml", "xhtml+xml")
                ):
                    raise ResearchVerificationError("research source content type is not textual")
                chunks: list[bytes] = []
                size = 0
                while True:
                    self._remaining(deadline)
                    chunk = response.read1(min(65_536, _MAX_BODY + 1 - size))
                    self._remaining(deadline)
                    if not chunk:
                        break
                    chunks.append(chunk)
                    size += len(chunk)
                    if size > _MAX_BODY:
                        raise ResearchVerificationError("research source exceeds byte limit")
                body = b"".join(chunks)
                return body, current, pinned_ip
            except (OSError, ssl.SSLError, http.client.HTTPException) as exc:
                raise ResearchVerificationError("research source fetch failed") from exc
            finally:
                connection.close()
        raise ResearchVerificationError("research source has too many redirects")

    def _remaining(self, deadline: float) -> float:
        remaining = deadline - self.clock()
        if remaining <= 0:
            raise ResearchVerificationError("research source fetch deadline exceeded")
        return remaining


def _normalize(value: str) -> str:
    return _SPACE.sub(" ", value).strip()
