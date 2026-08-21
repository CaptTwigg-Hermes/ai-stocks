#!/opt/hermes/.venv/bin/python
"""Credential-free MCP tool for bounded public HTTPS document retrieval."""

from __future__ import annotations

import html
import http.client
import ipaddress
import json
import os
import re
import socket
import ssl
import subprocess
import sys
import time
import unicodedata
from datetime import datetime, timezone
from html.parser import HTMLParser

from urllib.parse import urljoin, urlsplit, urlunsplit

MAXIMUM_RESPONSE_BYTES = 512 * 1024
MAXIMUM_VISIBLE_CHARACTERS = 30_000
MAXIMUM_STRUCTURED_CHARACTERS = 30_000
MAXIMUM_EVIDENCE_EXCERPT_UTF16 = 4_000
MAXIMUM_PUBLICATION_ITEM_CHARACTERS = 512
MAXIMUM_REDIRECTS = 4
MAXIMUM_OPERATION_SECONDS = 15
PUBLICATION_KEYS = {
    "article:published_time",
    "datepublished",
    "date",
}
VOID_ELEMENTS = {
    "area", "base", "br", "col", "embed", "hr", "img", "input",
    "link", "meta", "param", "source", "track", "wbr",
}
DOTNET_WHITESPACE_RE = re.compile(
    r"[\u0009-\u000d\u0020\u0085\u00a0\u1680\u2000-\u200a\u2028\u2029\u202f\u205f\u3000]+"
)


def validate_url(url: str) -> tuple[str, str]:
    if not isinstance(url, str):
        raise ValueError("URL is missing or too long.")
    try:
        utf16_length = len(url.encode("utf-16-le")) // 2
    except UnicodeEncodeError as exception:
        raise ValueError("URL contains invalid Unicode.") from exception
    if utf16_length > 2048:
        raise ValueError("URL is missing or too long.")
    parsed = urlsplit(url)
    if parsed.scheme.lower() != "https" or not parsed.hostname or parsed.fragment:
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
        try:
            socket.inet_aton(host)
        except OSError:
            pass
        else:
            raise ValueError("Legacy IPv4 evidence URLs are forbidden.")
        labels = host.split(".")
        if (len(labels) < 2 or any(
                not label or len(label) > 63 or
                re.fullmatch(r"[a-z0-9](?:[a-z0-9-]*[a-z0-9])?", label) is None
                for label in labels)):
            raise ValueError("Evidence URL must identify a qualified public DNS host.")
    else:
        raise ValueError("IP-literal evidence URLs are forbidden.")
    path = urlunsplit(("", "", parsed.path or "/", parsed.query, ""))
    return host, path


def _remaining_seconds(deadline: float) -> float:
    remaining = deadline - time.monotonic()
    if remaining <= 0:
        raise ValueError("Public HTTPS fetch exceeded its total operation deadline.")
    return remaining


def resolve_public_addresses(host: str, deadline: float | None = None) -> list[str]:
    records = socket.getaddrinfo(host, 443, type=socket.SOCK_STREAM)
    if deadline is not None:
        _remaining_seconds(deadline)
    addresses = sorted({str(item[4][0]) for item in records})
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


def _read_response(url: str, deadline: float | None = None) -> tuple[int, dict[str, str], bytes]:
    deadline = deadline if deadline is not None else time.monotonic() + MAXIMUM_OPERATION_SECONDS
    host, path = validate_url(url)
    addresses = resolve_public_addresses(host, deadline)
    last_error: Exception | None = None
    for address in addresses:
        raw = None
        wrapped = None
        try:
            raw = socket.create_connection((address, 443), timeout=min(5, _remaining_seconds(deadline)))
            wrapped = ssl.create_default_context().wrap_socket(raw, server_hostname=host)
            wrapped.settimeout(min(5, _remaining_seconds(deadline)))
            request = (
                f"GET {path} HTTP/1.1\r\nHost: {host}\r\n"
                "User-Agent: AiStocks-ResearchFetcher/1.0\r\n"
                "Accept: text/html, application/xhtml+xml, text/plain, application/json, application/xml\r\n"
                "Accept-Encoding: identity\r\nConnection: close\r\n\r\n"
            )
            wrapped.sendall(request.encode("ascii"))
            response = http.client.HTTPResponse(wrapped)
            response.begin()
            grouped_headers: dict[str, list[str]] = {}
            for key, value in response.getheaders():
                grouped_headers.setdefault(key.lower(), []).append(value)
            headers = {key: ",".join(values) for key, values in grouped_headers.items()}
            wrapped.settimeout(min(5, _remaining_seconds(deadline)))
            body = response.read(MAXIMUM_RESPONSE_BYTES + 1)
            _remaining_seconds(deadline)
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
        self.evidence_visible: list[str] = []
        self.publication: list[str] = []
        self.structured_blocks: list[str] = []
        self._structured_depth = 0
        self._hidden_depth = 0
        self._element_frames: list[tuple[str, bool]] = []
        self.has_external_stylesheet = False
        self.has_css = False
        self.has_unsupported_visibility = False
        self.has_visibility_controls = False
        self.has_structured_metadata = False

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        lower = tag.lower()
        if lower in {"head", "script", "style", "noscript", "svg", "template"}:
            self._blocked_depth += 1
        names = {key.lower() for key, _value in attrs}
        values: dict[str, str] = {}
        for key, value in attrs:
            values.setdefault(key.lower(), value or "")
        rel_tokens = [token.lower() for token in values.get("rel", "").split(" ") if token]
        if lower == "link" and "stylesheet" in rel_tokens:
            self.has_external_stylesheet = True
        if lower == "style" or "style" in names:
            self.has_css = True
        if lower in {"select", "datalist", "object", "iframe", "canvas", "audio", "video", "picture"} or "popover" in names:
            self.has_unsupported_visibility = True
        hidden = ("hidden" in names or values.get("aria-hidden", "").lower() == "true" or
                  lower == "dialog" and "open" not in names or
                  lower == "details" and "open" not in names)
        if hidden:
            self._hidden_depth += 1
            self.has_visibility_controls = True
        if lower not in VOID_ELEMENTS:
            self._element_frames.append((lower, hidden))
        if lower == "script" and values.get("type", "") == "application/ld+json":
            self._structured_depth += 1
            self.has_structured_metadata = True
        key = (values["property"] if "property" in values else
               values["name"] if "name" in values else
               values.get("itemprop", "")).lower()
        if lower == "meta" and key in PUBLICATION_KEYS and values.get("content"):
            self.publication.append(values["content"].strip())


    def handle_endtag(self, tag: str) -> None:
        lower = tag.lower()
        matching_index = next(
            (index for index in range(len(self._element_frames) - 1, -1, -1)
             if self._element_frames[index][0] == lower),
            None,
        )
        if matching_index is not None:
            removed = self._element_frames[matching_index:]
            del self._element_frames[matching_index:]
            self._hidden_depth -= sum(1 for _frame_tag, hidden in removed if hidden)
        if matching_index is not None and lower == "script" and self._structured_depth:
            self._structured_depth -= 1
        if (matching_index is not None and
                lower in {"head", "script", "style", "noscript", "svg", "template"} and self._blocked_depth):
            self._blocked_depth -= 1

    def handle_data(self, data: str) -> None:
        if self._structured_depth:
            self.structured_blocks.append(data)
        if not self._blocked_depth and not self._hidden_depth:
            value = re.sub(r"\s+", " ", html.unescape(data)).strip()
            if value:
                self.visible.append(value)
            if not any(
                unicodedata.category(character).startswith("C")
                and DOTNET_WHITESPACE_RE.fullmatch(character) is None
                for character in data
            ):
                evidence_value = DOTNET_WHITESPACE_RE.sub(" ", data).strip(" ")
                if evidence_value:
                    self.evidence_visible.append(evidence_value)


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
    charset_match = re.search(r"charset\s*=\s*([^;\s]+)", content_type, re.IGNORECASE)
    charset = charset_match.group(1).strip("\"'") if charset_match else "utf-8"
    if charset.lower() != "utf-8":
        raise ValueError("Evidence content encoding is invalid or unsupported.")
    try:
        text = body.decode("utf-8")
    except UnicodeDecodeError as exception:
        raise ValueError("Evidence content encoding is invalid or unsupported.") from exception
    publication: list[str] = []
    authority_publication: list[str] = []
    structured: list[str] = []
    structured_publication: list[str] = []
    malformed_structured_metadata = False
    discovery_segments: list[str] = []
    evidence_segments: list[str] = []
    has_external_stylesheet = False
    has_css = False
    has_unsupported_visibility = False
    has_visibility_controls = False
    has_structured_metadata = False
    if media_type in {"text/html", "application/xhtml+xml"}:
        parser = _DocumentParser()
        parser.feed(text)
        visible = "\n".join(parser.visible)
        discovery_segments = parser.visible
        evidence_segments = parser.evidence_visible
        publication = [
            value[:MAXIMUM_PUBLICATION_ITEM_CHARACTERS]
            for value in dict.fromkeys(parser.publication)
        ][:20]
        authority_publication = parser.publication
        has_external_stylesheet = parser.has_external_stylesheet
        has_css = parser.has_css
        has_unsupported_visibility = parser.has_unsupported_visibility
        has_visibility_controls = parser.has_visibility_controls
        has_structured_metadata = parser.has_structured_metadata
        for block in parser.structured_blocks:
            try:
                value = _load_verifier_json(block)
                _collect_structured_article_text(value, structured)
                _collect_json_publication_times(value, structured_publication)
            except (TypeError, ValueError, json.JSONDecodeError):
                malformed_structured_metadata = True
                continue
    else:
        visible = re.sub(r"\s+", " ", text).strip()
    structured = _bounded_unique_text(structured, MAXIMUM_STRUCTURED_CHARACTERS)
    verifier_time = _one_verifier_publication_time(authority_publication + structured_publication)
    if malformed_structured_metadata:
        eligible = False
        reason = "malformed-structured-metadata"
        candidates = []
    elif verifier_time is None:
        eligible = False
        reason = "ambiguous-publication-time"
        candidates: list[str] = []
    elif has_external_stylesheet or has_css:
        candidates = structured
        eligible = bool(candidates)
        reason = None if eligible else "external-stylesheet-without-structured-article-text"
    elif has_unsupported_visibility or has_visibility_controls:
        candidates = []
        eligible = False
        reason = "unsupported-visibility-semantics"
    elif has_structured_metadata:
        candidates = []
        eligible = False
        reason = "no-verifier-aligned-structured-representation"
    else:
        candidates = _bounded_unique_text(evidence_segments, MAXIMUM_VISIBLE_CHARACTERS)
        eligible = bool(candidates)
        reason = None if eligible else "no-verifier-aligned-visible-text"
    return {
        "url": url,
        "content_type": content_type,
        "publication_metadata": publication,
        "structured_article_text": structured,
        "visible_text": visible[:MAXIMUM_VISIBLE_CHARACTERS],
        "discovery_text": visible[:MAXIMUM_VISIBLE_CHARACTERS],
        "evidence_candidates": candidates,
        "verifier_publication_time": verifier_time,
        "verifier_eligible": eligible,
        "ineligibility_reason": reason,
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
            normalized = DOTNET_WHITESPACE_RE.sub(" ", text).strip(" ")
            unstable = "&" in normalized or any(
                unicodedata.category(character).startswith("C") for character in normalized
            )
            if normalized and not unstable:
                output.append(normalized)


def _load_verifier_json(text: str) -> object:
    def reject_constant(value: str) -> None:
        raise ValueError(f"Non-standard JSON constant {value} is forbidden.")

    def unique_object(pairs: list[tuple[str, object]]) -> dict[str, object]:
        output: dict[str, object] = {}
        for key, item in pairs:
            if key in output:
                raise ValueError(f"Duplicate JSON property {key} is conservatively rejected.")
            output[key] = item
        return output

    value = json.loads(text, parse_constant=reject_constant, object_pairs_hook=unique_object)
    if _json_depth(value) > 63:
        raise ValueError("JSON exceeds the verifier depth limit.")
    return value


def _json_depth(value: object) -> int:
    if isinstance(value, dict):
        return 1 + max((_json_depth(item) for item in value.values()), default=0)
    if isinstance(value, list):
        return 1 + max((_json_depth(item) for item in value), default=0)
    return 0


def _collect_json_publication_times(value: object, output: list[str]) -> None:
    if isinstance(value, list):
        for item in value:
            _collect_json_publication_times(item, output)
    elif isinstance(value, dict):
        for key, item in value.items():
            if key.lower() == "datepublished" and isinstance(item, str):
                output.append(item.strip())
            _collect_json_publication_times(item, output)


def _one_verifier_publication_time(values: list[str]) -> str | None:
    parsed: dict[int, str] = {}
    pattern = re.compile(
        r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.(\d{1,7}))?(Z|[+-]\d{2}:\d{2})"
    )
    for value in values:
        match = pattern.fullmatch(value)
        if match is None:
            continue
        if value[-1] != "Z":
            offset_hours = int(value[-5:-3])
            offset_minutes = int(value[-2:])
            if offset_hours > 14 or offset_minutes > 59 or (offset_hours == 14 and offset_minutes != 0):
                continue
        try:
            timestamp = datetime.fromisoformat(value.replace("Z", "+00:00"))
        except ValueError:
            continue
        if timestamp.tzinfo is None:
            continue
        utc = timestamp.astimezone(timezone.utc)
        delta = utc - datetime(1970, 1, 1, tzinfo=timezone.utc)
        seventh_digit = int((match.group(1) or "").ljust(7, "0")[6])
        ticks = ((delta.days * 86_400 + delta.seconds) * 10_000_000 +
                 utc.microsecond * 10 + seventh_digit)
        parsed.setdefault(ticks, value)
    return next(iter(parsed.values())) if len(parsed) == 1 else None


def _bounded_unique_text(values: list[str], maximum_characters: int) -> list[str]:
    output: list[str] = []
    seen: set[str] = set()
    remaining = maximum_characters
    for value in values:
        if value in seen or remaining <= 0:
            continue
        seen.add(value)
        bounded = _truncate_utf16(value, MAXIMUM_EVIDENCE_EXCERPT_UTF16)[:remaining]
        if bounded:
            output.append(bounded)
            remaining -= len(bounded)
    return output


def _truncate_utf16(value: str, maximum_units: int) -> str:
    encoded = value.encode("utf-16-le")
    if len(encoded) <= maximum_units * 2:
        return value
    return encoded[:maximum_units * 2].decode("utf-16-le", errors="ignore")


def _fetch_public_https_in_process(url: str) -> dict[str, object]:
    current = url
    deadline = time.monotonic() + MAXIMUM_OPERATION_SECONDS
    for redirects in range(MAXIMUM_REDIRECTS + 1):
        _remaining_seconds(deadline)
        status, headers, body = _read_response(current, deadline)
        _remaining_seconds(deadline)
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
        document = extract_document(current, headers.get("content-type", ""), body)
        _remaining_seconds(deadline)
        return document
    raise ValueError("Redirect limit exceeded.")


def _fetch_child_command(url: str) -> list[str]:
    return [sys.executable, os.path.abspath(__file__), "--fetch-child", url]


def fetch_public_https(url: str) -> dict[str, object]:
    validate_url(url)
    environment = {
        "PATH": os.environ.get("PATH", "/usr/local/bin:/usr/bin:/bin"),
        "PYTHONIOENCODING": "utf-8",
    }
    try:
        completed = subprocess.run(
            _fetch_child_command(url),
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            timeout=MAXIMUM_OPERATION_SECONDS,
            check=False,
            env=environment,
        )
    except subprocess.TimeoutExpired as exception:
        raise ValueError("Public HTTPS fetch exceeded its total operation deadline.") from exception
    if completed.returncode != 0:
        detail = completed.stderr.strip()[-512:]
        raise ValueError(detail or "Public HTTPS fetch failed closed.")
    try:
        result = json.loads(completed.stdout)
    except json.JSONDecodeError as exception:
        raise ValueError("Public HTTPS fetch returned an invalid child response.") from exception
    if not isinstance(result, dict):
        raise ValueError("Public HTTPS fetch returned an invalid child response.")
    return result


def main() -> None:
    from mcp.server.fastmcp import FastMCP

    server = FastMCP("research")

    @server.tool()
    def fetch_public_https_tool(url: str) -> str:
        """Fetch one public HTTPS page. discovery_text is discovery-only; evidence may use only evidence_candidates when verifier_eligible is true. No local files, private addresses, credentials, scripts, or shell commands are accessible."""
        return json.dumps(fetch_public_https(url), ensure_ascii=False)

    server.run(transport="stdio")


if __name__ == "__main__":
    if len(sys.argv) == 3 and sys.argv[1] == "--fetch-child":
        try:
            print(json.dumps(_fetch_public_https_in_process(sys.argv[2]), ensure_ascii=True))
        except Exception as exception:
            print(str(exception), file=sys.stderr)
            raise SystemExit(1)
    else:
        main()
