import hashlib
import json
from datetime import UTC, datetime

import httpx
import pytest

from ai_stocks.nasdaq_acquisition import FeedProtocolError, NasdaqPostTradeClient

CSV = b'"sep=;"\r\nTrading date and time;Venue of execution\r\n'
REPORT = "NordicEquity-posttrade-2026-08-06T1500"


def client_for(handler, archive_dir):
    return NasdaqPostTradeClient(
        archive_dir=archive_dir,
        http=httpx.Client(
            base_url="https://tradereports.nasdaq.com",
            transport=httpx.MockTransport(handler),
        ),
        clock=lambda: datetime(2026, 8, 6, 15, 16, tzinfo=UTC),
    )


def test_lists_strict_reports_and_archives_raw_csv_with_provenance(tmp_path):
    requests = []

    def handler(request):
        requests.append(request)
        if request.url.path.endswith("trade-reports"):
            return httpx.Response(200, json={"message": None, "reports": [REPORT]})
        return httpx.Response(200, content=CSV)

    feed = client_for(handler, tmp_path)
    assert feed.list_reports() == (REPORT,)
    archived = feed.download_and_archive(REPORT)

    assert archived.csv_path.read_bytes() == CSV
    metadata = json.loads(archived.metadata_path.read_text())
    assert metadata == {
        "bytes": len(CSV),
        "fetched_at": "2026-08-06T15:16:00+00:00",
        "report": REPORT,
        "sha256": hashlib.sha256(CSV).hexdigest(),
        "source_url": (
            "https://tradereports.nasdaq.com/api/regulatory/trade-report/download"
            "?type=POST_TRADE&assetClass=EQUITY&fileName=" + REPORT
        ),
    }
    assert requests[0].url.path == "/api/regulatory/trade-reports"
    assert requests[1].url.path == "/api/regulatory/trade-report/download"


def test_existing_archive_is_verified_and_never_refetched_or_overwritten(tmp_path):
    calls = 0

    def handler(_request):
        nonlocal calls
        calls += 1
        return httpx.Response(200, content=CSV)

    feed = client_for(handler, tmp_path)
    first = feed.download_and_archive(REPORT)
    second = feed.download_and_archive(REPORT)

    assert second == first
    assert calls == 1


def test_malformed_listing_and_report_name_fail_closed(tmp_path):
    feed = client_for(
        lambda _request: httpx.Response(
            200,
            json={"message": None, "reports": ["../../secret"]},
        ),
        tmp_path,
    )
    with pytest.raises(FeedProtocolError):
        feed.list_reports()
    with pytest.raises(FeedProtocolError):
        feed.download_and_archive("../../secret")


def test_tampered_archive_and_non_csv_response_fail_closed(tmp_path):
    feed = client_for(lambda _request: httpx.Response(200, content=CSV), tmp_path)
    archived = feed.download_and_archive(REPORT)
    archived.csv_path.write_bytes(b"tampered")
    with pytest.raises(FeedProtocolError, match="checksum"):
        feed.download_and_archive(REPORT)

    other = "NordicEquity-posttrade-2026-08-06T1501"
    invalid = client_for(
        lambda _request: httpx.Response(200, content=b"<html>error</html>"), tmp_path
    )
    with pytest.raises(FeedProtocolError, match="expected CSV"):
        invalid.download_and_archive(other)
