import ipaddress
import json
import os
import re
import threading
from collections.abc import Callable, Mapping
from dataclasses import dataclass
from datetime import UTC, datetime, timedelta
from typing import Any
from urllib.parse import urlsplit

import httpx
import jwt
from jwt.algorithms import RSAAlgorithm

MAX_ASSERTION_BYTES = 16_384
MAX_JWKS_BYTES = 262_144


class AuthenticationError(Exception):
    """An Access assertion could not be authenticated or locally authorized."""


@dataclass(frozen=True)
class AccessIdentity:
    email: str
    role: str


@dataclass(frozen=True)
class AccessConfig:
    team_domain: str
    audience: str
    public_origin: str
    owner_emails: frozenset[str]
    viewer_emails: frozenset[str]
    allow_any_authenticated_viewer: bool = False

    def __post_init__(self):
        team = _validated_origin(self.team_domain, cloudflare_team=True)
        origin = _validated_origin(self.public_origin, cloudflare_team=False)
        audience = self.audience.strip()
        owners = _validated_emails(self.owner_emails, "owner")
        if not isinstance(self.allow_any_authenticated_viewer, bool):
            raise RuntimeError("ACCESS_ALLOW_ANY_AUTHENTICATED_VIEWER must be true or false")
        if self.allow_any_authenticated_viewer:
            if self.viewer_emails:
                raise RuntimeError(
                    "ACCESS_VIEWER_EMAILS must be empty when Cloudflare authorizes all viewers"
                )
            viewers = frozenset()
        else:
            viewers = _validated_emails(self.viewer_emails, "viewer")
        if owners & viewers:
            raise RuntimeError("owner and viewer email allowlists must not overlap")
        if not audience or audience == "*" or len(audience) > 512:
            raise RuntimeError("ACCESS_AUD must be a non-wildcard application audience")
        object.__setattr__(self, "team_domain", team)
        object.__setattr__(self, "public_origin", origin)
        object.__setattr__(self, "audience", audience)
        object.__setattr__(self, "owner_emails", owners)
        object.__setattr__(self, "viewer_emails", viewers)

    @classmethod
    def from_env(cls):
        allow_any_value = (
            os.getenv("ACCESS_ALLOW_ANY_AUTHENTICATED_VIEWER", "false").strip().lower()
        )
        if allow_any_value not in {"true", "false"}:
            raise RuntimeError("ACCESS_ALLOW_ANY_AUTHENTICATED_VIEWER must be true or false")
        allow_any = allow_any_value == "true"
        required = {
            "team_domain": "ACCESS_TEAM_DOMAIN",
            "audience": "ACCESS_AUD",
            "public_origin": "PUBLIC_ORIGIN",
            "owner_emails": "ACCESS_OWNER_EMAILS",
        }
        values: dict[str, Any] = {}
        missing = []
        for field, name in required.items():
            value = os.getenv(name, "").strip()
            if not value:
                missing.append(name)
            elif field.endswith("emails"):
                values[field] = frozenset(part.strip() for part in value.split(",") if part.strip())
            else:
                values[field] = value
        if missing:
            raise RuntimeError(f"missing required Access configuration: {', '.join(missing)}")
        viewer_value = os.getenv("ACCESS_VIEWER_EMAILS", "").strip()
        values["viewer_emails"] = frozenset(
            part.strip() for part in viewer_value.split(",") if part.strip()
        )
        values["allow_any_authenticated_viewer"] = allow_any
        return cls(**values)

    @property
    def issuer(self):
        return self.team_domain

    @property
    def jwks_url(self):
        return f"{self.team_domain}/cdn-cgi/access/certs"


def _validated_origin(value: str, *, cloudflare_team: bool) -> str:
    parsed = urlsplit(value.strip())
    try:
        port = parsed.port
    except ValueError as exc:
        raise RuntimeError("origin has an invalid port") from exc
    if (
        parsed.scheme != "https"
        or not parsed.hostname
        or parsed.username is not None
        or parsed.password is not None
        or port is not None
        or parsed.path not in ("", "/")
        or parsed.query
        or parsed.fragment
    ):
        raise RuntimeError("origin must be an HTTPS origin without port, path, query, or fragment")
    hostname = parsed.hostname.lower().rstrip(".")
    try:
        ipaddress.ip_address(hostname)
    except ValueError:
        labels = hostname.split(".")
        label_pattern = re.compile(r"^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$", re.IGNORECASE)
        if len(labels) < 2 or any(not label_pattern.fullmatch(label) for label in labels):
            raise RuntimeError("origin must use an exact public DNS hostname") from None
    else:
        raise RuntimeError("origin cannot use an IP literal")
    if cloudflare_team and (
        not hostname.endswith(".cloudflareaccess.com") or hostname == "cloudflareaccess.com"
    ):
        raise RuntimeError("ACCESS_TEAM_DOMAIN must be an exact *.cloudflareaccess.com domain")
    return f"https://{hostname}"


def _normal_email(value: object) -> str:
    if not isinstance(value, str):
        raise ValueError("email must be a string")
    email = value.strip().lower()
    if (
        not email
        or len(email) > 254
        or email.count("@") != 1
        or any(ord(char) < 33 or ord(char) == 127 for char in email)
    ):
        raise ValueError("malformed email")
    local, domain = email.split("@")
    if (
        not local
        or not domain
        or domain.startswith(".")
        or domain.endswith(".")
        or "." not in domain
    ):
        raise ValueError("malformed email")
    return email


def _validated_emails(values: frozenset[str], role: str) -> frozenset[str]:
    try:
        normalized = frozenset(_normal_email(value) for value in values)
    except ValueError as exc:
        raise RuntimeError(f"invalid {role} email allowlist") from exc
    if not normalized:
        raise RuntimeError(f"ACCESS_{role.upper()}_EMAILS must not be empty")
    return normalized


class AccessJWTValidator:
    def __init__(
        self,
        config: AccessConfig,
        *,
        fetcher: Callable[[str], Mapping[str, Any]] | None = None,
        clock: Callable[[], datetime] | None = None,
        cache_ttl: timedelta = timedelta(minutes=5),
        refresh_cooldown: timedelta = timedelta(seconds=30),
    ):
        if cache_ttl <= timedelta(0) or refresh_cooldown <= timedelta(0):
            raise ValueError("JWKS cache durations must be positive")
        self.config = config
        self._fetcher = fetcher or self._fetch_jwks
        self._clock = clock or (lambda: datetime.now(UTC))
        self._cache_ttl = cache_ttl
        self._refresh_cooldown = refresh_cooldown
        self._keys: dict[str, Any] = {}
        self._cache_expires_at: datetime | None = None
        self._last_refresh_attempt: datetime | None = None
        self._refresh_lock = threading.Lock()

    def validate(self, assertion: str) -> AccessIdentity:
        if (
            not isinstance(assertion, str)
            or not assertion
            or len(assertion.encode()) > MAX_ASSERTION_BYTES
        ):
            raise AuthenticationError("invalid Access assertion")
        if assertion.count(".") != 2:
            raise AuthenticationError("invalid Access assertion")
        try:
            header = jwt.get_unverified_header(assertion)
        except jwt.PyJWTError as exc:
            raise AuthenticationError("invalid Access assertion") from exc
        if header.get("alg") != "RS256":
            raise AuthenticationError("Access assertion algorithm must be RS256")
        kid = header.get("kid")
        if not isinstance(kid, str) or not kid or len(kid) > 256:
            raise AuthenticationError("Access assertion has invalid kid")

        now = self._aware_now()
        key = self._keys.get(kid)
        cache_stale = self._cache_expires_at is None or now >= self._cache_expires_at
        if key is None or cache_stale:
            self._refresh(now)
            key = self._keys.get(kid)
        if key is None:
            raise AuthenticationError("Access signing key is unavailable")

        try:
            claims = jwt.decode(
                assertion,
                key=key,
                algorithms=["RS256"],
                options={
                    "verify_aud": False,
                    "verify_iss": False,
                    "verify_exp": False,
                    "verify_nbf": False,
                },
            )
        except jwt.PyJWTError as exc:
            raise AuthenticationError("invalid Access assertion signature") from exc
        self._validate_claims(claims, now)
        try:
            email = _normal_email(claims.get("email"))
        except ValueError as exc:
            raise AuthenticationError("Access assertion has invalid email") from exc
        if email in self.config.owner_emails:
            return AccessIdentity(email=email, role="owner")
        if self.config.allow_any_authenticated_viewer:
            return AccessIdentity(email=email, role="viewer")
        if email in self.config.viewer_emails:
            return AccessIdentity(email=email, role="viewer")
        raise AuthenticationError("identity is not authorized")

    def _aware_now(self) -> datetime:
        now = self._clock()
        if now.tzinfo is None:
            raise RuntimeError("authentication clock must be timezone-aware")
        return now.astimezone(UTC)

    def _refresh(self, now: datetime) -> None:
        with self._refresh_lock:
            if (
                self._last_refresh_attempt is not None
                and now - self._last_refresh_attempt < self._refresh_cooldown
            ):
                return
            # Record before outbound I/O so failures and malformed responses are throttled too.
            self._last_refresh_attempt = now
            try:
                document = self._fetcher(self.config.jwks_url)
                candidate = self._parse_keys(document)
            # An injected or standard-library fetcher may expose different network/protocol
            # exception types. Authentication must fail closed rather than turn them into 500s.
            except Exception:  # noqa: BLE001
                return
            self._keys = candidate
            self._cache_expires_at = now + self._cache_ttl

    @staticmethod
    def _parse_keys(document: Mapping[str, Any]) -> dict[str, Any]:
        if not isinstance(document, Mapping) or not isinstance(document.get("keys"), list):
            raise ValueError("malformed JWKS")
        result = {}
        for item in document["keys"]:
            if not isinstance(item, Mapping):
                continue
            kid = item.get("kid")
            if isinstance(kid, str) and kid in result:
                raise ValueError("JWKS contains duplicate kid")
            if (
                item.get("kty") != "RSA"
                or not isinstance(kid, str)
                or not kid
                or len(kid) > 256
                or item.get("alg") not in (None, "RS256")
                or item.get("use") not in (None, "sig")
            ):
                continue
            try:
                result[kid] = RSAAlgorithm.from_jwk(dict(item))
            except (ValueError, TypeError, KeyError, jwt.PyJWTError):
                continue
        if not result:
            raise ValueError("JWKS contains no usable RS256 keys")
        return result

    def _validate_claims(self, claims: Mapping[str, Any], now: datetime) -> None:
        if claims.get("iss") != self.config.issuer:
            raise AuthenticationError("Access assertion issuer mismatch")
        audience = claims.get("aud")
        if isinstance(audience, str):
            audiences = [audience]
        elif isinstance(audience, list) and all(isinstance(value, str) for value in audience):
            audiences = audience
        else:
            raise AuthenticationError("Access assertion has invalid audience")
        if self.config.audience not in audiences:
            raise AuthenticationError("Access assertion audience mismatch")
        exp = claims.get("exp")
        nbf = claims.get("nbf")
        if isinstance(exp, bool) or not isinstance(exp, int):
            raise AuthenticationError("Access assertion exp must be an integer")
        if isinstance(nbf, bool) or not isinstance(nbf, int):
            raise AuthenticationError("Access assertion nbf must be an integer")
        timestamp = int(now.timestamp())
        if exp <= timestamp:
            raise AuthenticationError("Access assertion expired")
        if nbf > timestamp:
            raise AuthenticationError("Access assertion is not yet valid")

    @staticmethod
    def _fetch_jwks(url: str) -> Mapping[str, Any]:
        headers = {"Accept": "application/json", "User-Agent": "ai-stocks/1"}
        with httpx.stream(
            "GET", url, headers=headers, timeout=5, follow_redirects=False
        ) as response:
            response.raise_for_status()
            content_length = response.headers.get("Content-Length")
            if content_length and int(content_length) > MAX_JWKS_BYTES:
                raise ValueError("JWKS response too large")
            body = bytearray()
            for chunk in response.iter_bytes():
                body.extend(chunk)
                if len(body) > MAX_JWKS_BYTES:
                    raise ValueError("JWKS response too large")
        if len(body) > MAX_JWKS_BYTES:
            raise ValueError("JWKS response too large")
        parsed = json.loads(body)
        if not isinstance(parsed, Mapping):
            raise ValueError("malformed JWKS")
        return parsed
