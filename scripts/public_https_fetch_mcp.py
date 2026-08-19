#!/opt/hermes/.venv/bin/python
"""Credential-free MCP tool for bounded public HTTPS document retrieval."""

from __future__ import annotations

import html
import http.client
import ipaddress
import json
import re
import socket
import ssl
from html.parser import HTMLParser
from urllib.parse import urljoin, urlsplit, urlunsplit

MAXIMUM_RESPONSE_BYTES = 512 * 1024
MAXIMUM_VISIBLE_CHARACTERS = 30_000
MAXIMUM_STRUCTURED_CHARACTERS = 30_000
MAXIMUM_PUBLICATION_ITEM_CHARACTERS = 512
MAXIMUM_REDIRECTS = 4
PUBLICATION_KEYS = {
    "article:published_time",
    "datepublished",
    "date",
    "dc.date",
    "dc.date.issued",
    "publishdate",
    "pubdate",
}


def validate_url(url: str) -> tuple[str, str]:
    if not isinstance(url, str) or len(url) > 4096:
        raise ValueError("URL is missing or too long.")
    parsed = urlsplit(url)
    if parsed.scheme.lower() != "https" or not parsed.hostname:
        raise ValueError("Only absolute HTTPS URLs are allowed.")
    if parsed.username is not None or parsed.password is not None:
        raise ValueError("Credential-bearing URLs are forbidden.")
    if parsed.port not in (None, 443):
        raise ValueError("Only the standard HTTPS port is allowed.")
    host = parsed.hostname.rstrip(".").encode("idna").decode("ascii").lower()
    if host == "localhost" or host.endswith(".localhost"):
        raise ValueError("Local destinations are forbidden.")
    try:
        literal = ipaddress.ip_address(host)
    except ValueError:
        pass
    else:
        if not _is_public_address(literal):
            raise ValueError("Non-public destinations are forbidden.")
    path = urlunsplit(("", "", parsed.path or "/", parsed.query, ""))
    return host, path


def resolve_public_addresses(host: str) -> list[str]:
    addresses = sorted({str(item[4][0]) for item in socket.getaddrinfo(host, 443, type=socket.SOCK_STREAM)})
    if not addresses:
        raise ValueError("The destination did not resolve.")
    if any(not _is_public_address(ipaddress.ip_address(address)) for address in addresses):
        raise ValueError("The destination resolved to a non-public address.")
    return addresses


def _is_public_address(address: ipaddress.IPv4Address | ipaddress.IPv6Address) -> bool:
    if isinstance(address, ipaddress.IPv4Address):
        return address.is_global
    if address.ipv4_mapped is not None:
        return False
    # Only native IPv6 global unicast is eligible. This deliberately rejects
    # NAT64, transition, documentation, benchmarking, and other special-use
    # prefixes even where Python's broad is_global flag reports True.
    if address not in ipaddress.ip_network("2000::/3"):
        return False
    return not any(
        address in network
        for network in (
            ipaddress.ip_network("2001::/23"),
            ipaddress.ip_network("2001:db8::/32"),
            ipaddress.ip_network("2002::/16"),
            ipaddress.ip_network("3fff::/20"),
        )
    )


def _read_response(url: str) -> tuple[int, dict[str, str], bytes]:
    host, path = validate_url(url)
    addresses = resolve_public_addresses(host)
    last_error: Exception | None = None
    for address in addresses:
        raw = None
        wrapped = None
        try:
            raw = socket.create_connection((address, 443), timeout=5)
            wrapped = ssl.create_default_context().wrap_socket(raw, server_hostname=host)
            request = (
                f"GET {path} HTTP/1.1\r\nHost: {host}\r\n"
                "User-Agent: AiStocks-ResearchFetcher/1.0\r\n"
                "Accept: text/html, application/xhtml+xml, text/plain, application/json, application/xml\r\n"
                "Accept-Encoding: identity\r\nConnection: close\r\n\r\n"
            )
            wrapped.sendall(request.encode("ascii"))
            response = http.client.HTTPResponse(wrapped)
            response.begin()
            headers = {key.lower(): value for key, value in response.getheaders()}
            body = response.read(MAXIMUM_RESPONSE_BYTES + 1)
            if len(body) > MAXIMUM_RESPONSE_BYTES:
                raise ValueError("Response exceeds the safe size limit.")
            return response.status, headers, body
        except (OSError, ssl.SSLError, http.client.HTTPException) as exc:
            last_error = exc
        finally:
            if wrapped is not None:
                wrapped.close()
            elif raw is not None:
                raw.close()
    raise ValueError("Unable to retrieve the public HTTPS document.") from last_error


class _DocumentParser(HTMLParser):
    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self._blocked_depth = 0
        self.visible: list[str] = []
        self.publication: list[str] = []
        self.structured_blocks: list[str] = []
        self._structured_depth = 0

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        lower = tag.lower()
        if lower in {"script", "style", "noscript", "svg", "template"}:
            self._blocked_depth += 1
        values = {key.lower(): value for key, value in attrs if value is not None}
        if lower == "script" and values.get("type", "").lower() == "application/ld+json":
            self._structured_depth += 1
        key = (values.get("property") or values.get("name") or "").lower()
        if lower == "meta" and key in PUBLICATION_KEYS and values.get("content"):
            self.publication.append(values["content"].strip())
        if lower == "time" and values.get("datetime"):
            self.publication.append(values["datetime"].strip())

    def handle_endtag(self, tag: str) -> None:
        if tag.lower() == "script" and self._structured_depth:
            self._structured_depth -= 1
        if tag.lower() in {"script", "style", "noscript", "svg", "template"} and self._blocked_depth:
            self._blocked_depth -= 1

    def handle_data(self, data: str) -> None:
        if self._structured_depth:
            self.structured_blocks.append(data)
        if not self._blocked_depth:
            value = re.sub(r"\s+", " ", html.unescape(data)).strip()
            if value:
                self.visible.append(value)


def extract_document(url: str, content_type: str, body: bytes) -> dict[str, object]:
    media_type = content_type.split(";", 1)[0].strip().lower()
    if media_type not in {
        "text/html",
        "application/xhtml+xml",
        "text/plain",
        "application/json",
        "application/xml",
        "text/xml",
    }:
        raise ValueError("The response is not a supported textual document.")
    charset_match = re.search(r"charset=([A-Za-z0-9._-]+)", content_type, re.IGNORECASE)
    charset = charset_match.group(1) if charset_match else "utf-8"
    try:
        text = body.decode(charset)
    except (LookupError, UnicodeDecodeError):
        text = body.decode("utf-8", errors="replace")
    publication: list[str] = []
    structured: list[str] = []
    if media_type in {"text/html", "application/xhtml+xml"}:
        parser = _DocumentParser()
        parser.feed(text)
        visible = "\n".join(parser.visible)
        publication = [
            value[:MAXIMUM_PUBLICATION_ITEM_CHARACTERS]
            for value in dict.fromkeys(parser.publication)
        ][:20]
        for block in parser.structured_blocks:
            try:
                _collect_structured_article_text(json.loads(block), structured)
            except (TypeError, ValueError, json.JSONDecodeError):
                continue
    else:
        visible = re.sub(r"\s+", " ", text).strip()
    return {
        "url": url,
        "content_type": content_type,
        "publication_metadata": publication,
        "structured_article_text": _bounded_unique_text(structured, MAXIMUM_STRUCTURED_CHARACTERS),
        "visible_text": visible[:MAXIMUM_VISIBLE_CHARACTERS],
        "truncated": len(visible) > MAXIMUM_VISIBLE_CHARACTERS,
    }


def _collect_structured_article_text(value: object, output: list[str]) -> None:
    if isinstance(value, list):
        for item in value:
            _collect_structured_article_text(item, output)
        return
    if not isinstance(value, dict):
        return
    if "@graph" in value:
        _collect_structured_article_text(value["@graph"], output)
    types = value.get("@type", [])
    if isinstance(types, str):
        types = [types]
    if not isinstance(types, list) or not {"Article", "NewsArticle", "PressRelease"}.intersection(types):
        return
    for key in ("headline", "description", "articleBody"):
        text = value.get(key)
        if isinstance(text, str):
            normalized = re.sub(r"\s+", " ", html.unescape(text)).strip()
            if normalized:
                output.append(normalized)


def _bounded_unique_text(values: list[str], maximum_characters: int) -> list[str]:
    output: list[str] = []
    seen: set[str] = set()
    remaining = maximum_characters
    for value in values:
        if value in seen or remaining <= 0:
            continue
        seen.add(value)
        bounded = value[:remaining]
        if bounded:
            output.append(bounded)
            remaining -= len(bounded)
    return output


def fetch_public_https(url: str) -> dict[str, object]:
    current = url
    for redirects in range(MAXIMUM_REDIRECTS + 1):
        status, headers, body = _read_response(current)
        if status in {301, 302, 303, 307, 308}:
            if redirects == MAXIMUM_REDIRECTS or "location" not in headers:
                raise ValueError("Redirect limit exceeded or Location is missing.")
            current = urljoin(current, headers["location"])
            validate_url(current)
            continue
        if status != 200:
            raise ValueError(f"Public HTTPS endpoint returned HTTP {status}.")
        if headers.get("content-encoding", "identity").lower() != "identity":
            raise ValueError("Compressed responses are not accepted.")
        return extract_document(current, headers.get("content-type", ""), body)
    raise ValueError("Redirect limit exceeded.")


def main() -> None:
    from mcp.server.fastmcp import FastMCP

    server = FastMCP("research")

    @server.tool()
    def fetch_public_https_tool(url: str) -> str:
        """Fetch one public HTTPS page and return bounded visible text and publication metadata. No local files, private addresses, credentials, scripts, or shell commands are accessible."""
        return json.dumps(fetch_public_https(url), ensure_ascii=False)

    server.run(transport="stdio")


if __name__ == "__main__":
    main()
