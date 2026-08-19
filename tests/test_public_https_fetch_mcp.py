import importlib.util
from pathlib import Path

import pytest


MODULE_PATH = Path(__file__).parents[1] / "scripts" / "public_https_fetch_mcp.py"


def load_module():
    spec = importlib.util.spec_from_file_location("public_https_fetch_mcp", MODULE_PATH)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def test_url_validation_rejects_non_https_credentials_and_private_literals():
    module = load_module()
    for url in (
        "http://example.com/news",
        "https://user:pass@example.com/news",
        "https://127.0.0.1/news",
        "https://[::1]/news",
        "https://[::ffff:127.0.0.1]/news",
        "https://[64:ff9b::7f00:1]/news",
        "https://[64:ff9b::a9fe:a9fe]/news",
        "https://localhost/news",
    ):
        with pytest.raises(ValueError):
            module.validate_url(url)


def test_visible_text_and_metadata_are_bounded_and_script_free():
    module = load_module()
    html = b'''<html><head><meta property="article:published_time" content="2026-08-19T08:00:00Z">
    <script type="application/ld+json">{"@context":"https://schema.org","@type":"NewsArticle","description":"Revenue rose 10 percent."}</script></head>
    <body><script>steal()</script><h1>Issuer result</h1><p>Revenue rose 10 percent.</p></body></html>'''
    result = module.extract_document("https://example.com/news", "text/html; charset=utf-8", html)
    assert "Issuer result" in result["visible_text"]
    assert "Revenue rose 10 percent." in result["visible_text"]
    assert "steal()" not in result["visible_text"]
    assert result["publication_metadata"] == ["2026-08-19T08:00:00Z"]
    assert result["structured_article_text"] == ["Revenue rose 10 percent."]
    assert len(result["visible_text"]) <= module.MAXIMUM_VISIBLE_CHARACTERS


def test_resolution_rejects_mixed_public_private_and_special_use(monkeypatch):
    module = load_module()
    monkeypatch.setattr(module.socket, "getaddrinfo", lambda *_args, **_kwargs: [
        (module.socket.AF_INET, module.socket.SOCK_STREAM, 6, "", ("93.184.216.34", 443)),
        (module.socket.AF_INET, module.socket.SOCK_STREAM, 6, "", ("127.0.0.1", 443)),
    ])
    with pytest.raises(ValueError, match="non-public"):
        module.resolve_public_addresses("example.com")


def test_redirect_is_revalidated_and_limits_and_compression_fail_closed(monkeypatch):
    module = load_module()
    monkeypatch.setattr(module, "_read_response", lambda _url: (
        302, {"location": "https://127.0.0.1/private"}, b""))
    with pytest.raises(ValueError, match="Non-public"):
        module.fetch_public_https("https://example.com/start")

    calls = []
    monkeypatch.setattr(module, "_read_response", lambda url: (
        calls.append(url) or (302, {"location": "https://example.com/next"}, b"")))
    with pytest.raises(ValueError, match="Redirect limit"):
        module.fetch_public_https("https://example.com/start")
    assert len(calls) == module.MAXIMUM_REDIRECTS + 1

    monkeypatch.setattr(module, "_read_response", lambda _url: (
        200, {"content-type": "text/html", "content-encoding": "gzip"}, b"compressed"))
    with pytest.raises(ValueError, match="Compressed"):
        module.fetch_public_https("https://example.com/news")


def test_transport_pins_resolved_address_and_enforces_response_byte_bound(monkeypatch):
    module = load_module()
    connections = []

    class FakeSocket:
        def sendall(self, _request):
            pass

        def close(self):
            pass

    class FakeContext:
        def wrap_socket(self, raw, server_hostname):
            assert server_hostname == "example.com"
            return raw

    class FakeResponse:
        status = 200

        def __init__(self, _wrapped):
            pass

        def begin(self):
            pass

        def getheaders(self):
            return [("Content-Type", "text/plain")]

        def read(self, amount):
            assert amount == module.MAXIMUM_RESPONSE_BYTES + 1
            return b"x" * amount

    monkeypatch.setattr(module, "resolve_public_addresses", lambda _host: ["93.184.216.34"])
    monkeypatch.setattr(module.socket, "create_connection", lambda endpoint, timeout: (
        connections.append((endpoint, timeout)) or FakeSocket()))
    monkeypatch.setattr(module.ssl, "create_default_context", lambda: FakeContext())
    monkeypatch.setattr(module.http.client, "HTTPResponse", FakeResponse)

    with pytest.raises(ValueError, match="size limit"):
        module._read_response("https://example.com/news")
    assert connections == [(("93.184.216.34", 443), 5)]


def test_structured_output_has_one_aggregate_character_bound():
    module = load_module()
    first = "a" * 20_000
    second = "b" * 20_000
    html = (
        '<script type="application/ld+json">'
        '{"@type":"NewsArticle","description":"' + first + '","articleBody":"' + second + '"}'
        '</script>'
    ).encode()
    result = module.extract_document("https://example.com/news", "text/html", html)
    assert sum(map(len, result["structured_article_text"])) == module.MAXIMUM_STRUCTURED_CHARACTERS