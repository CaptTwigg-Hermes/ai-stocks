import json
from datetime import UTC, datetime, timedelta

import jwt
import pytest
from cryptography.hazmat.primitives.asymmetric import rsa

from ai_stocks.auth import AccessConfig, AccessJWTValidator, AuthenticationError

NOW = datetime(2026, 8, 6, 10, tzinfo=UTC)
ISSUER = "https://contest.cloudflareaccess.com"


def keys(kid):
    private = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    public = json.loads(jwt.algorithms.RSAAlgorithm.to_jwk(private.public_key()))
    public.update(kid=kid, alg="RS256", use="sig")
    return private, public


def config(**changes):
    data = dict(
        team_domain=ISSUER,
        audience="aud",
        public_origin="https://stocks.example.com",
        owner_emails=frozenset({"owner@example.com"}),
        viewer_emails=frozenset({"viewer@example.com"}),
    )
    data.update(changes)
    return AccessConfig(**data)


def token(private, kid="key", **changes):
    claims = dict(
        iss=ISSUER,
        aud="aud",
        exp=int((NOW + timedelta(hours=1)).timestamp()),
        nbf=int((NOW - timedelta(minutes=1)).timestamp()),
        email="viewer@example.com",
    )
    claims.update(changes)
    return jwt.encode(claims, private, algorithm="RS256", headers={"kid": kid})


@pytest.mark.parametrize(
    "change",
    [
        {"team_domain": "http://contest.cloudflareaccess.com"},
        {"team_domain": "https://example.com"},
        {"team_domain": "https://*.cloudflareaccess.com"},
        {"public_origin": "https://stocks.example.com:8443"},
        {"public_origin": "https://localhost"},
        {"public_origin": "https://127.0.0.1"},
        {"public_origin": "https://10.0.0.1"},
        {"public_origin": "https://*.example.com"},
        {"public_origin": "https://singlelabel"},
        {"audience": "*"},
        {"owner_emails": frozenset()},
        {"viewer_emails": frozenset({"bad"})},
        {"viewer_emails": frozenset({"owner@example.com"})},
    ],
)
def test_configuration_fails_closed(change):
    with pytest.raises(RuntimeError):
        config(**change)


def test_valid_signature_exact_claims_and_local_roles():
    private, public = keys("key")
    calls = []
    check = AccessJWTValidator(
        config(), fetcher=lambda url: calls.append(url) or {"keys": [public]}, clock=lambda: NOW
    )
    assert check.validate(token(private)).role == "viewer"
    assert check.validate(token(private, email="OWNER@example.com")).role == "owner"
    assert calls == [ISSUER + "/cdn-cgi/access/certs"]


def test_cloudflare_policy_may_authorize_all_verified_non_owner_viewers():
    private, public = keys("key")
    check = AccessJWTValidator(
        config(
            viewer_emails=frozenset(),
            allow_any_authenticated_viewer=True,
        ),
        fetcher=lambda _: {"keys": [public]},
        clock=lambda: NOW,
    )
    assert (
        check.validate(token(private, email="allowed-by-cloudflare@example.com")).role == "viewer"
    )
    assert check.validate(token(private, email="OWNER@example.com")).role == "owner"


@pytest.mark.parametrize(
    "change",
    [
        {"iss": "https://other.cloudflareaccess.com"},
        {"aud": "wrong"},
        {"exp": int(NOW.timestamp())},
        {"exp": "9999999999"},
        {"nbf": int((NOW + timedelta(seconds=1)).timestamp())},
        {"nbf": True},
        {"email": "stranger@example.com"},
    ],
)
def test_claims_and_membership_fail_closed(change):
    private, public = keys("key")
    check = AccessJWTValidator(config(), fetcher=lambda _: {"keys": [public]}, clock=lambda: NOW)
    with pytest.raises(AuthenticationError):
        check.validate(token(private, **change))


def test_algorithm_kid_signature_size_and_duplicate_jwks_fail_closed():
    private, public = keys("key")
    other, _ = keys("other")
    check = AccessJWTValidator(
        config(), fetcher=lambda _: {"keys": [public, public]}, clock=lambda: NOW
    )
    for assertion in (token(private), token(other), "x" * 20_000, "not.a.jwt"):
        with pytest.raises(AuthenticationError):
            check.validate(assertion)


def test_unknown_kid_failed_refresh_throttled_before_io():
    private, _ = keys("missing")
    attempts = 0

    def fetch(_):
        nonlocal attempts
        attempts += 1
        raise OSError("offline")

    check = AccessJWTValidator(
        config(), fetcher=fetch, clock=lambda: NOW, refresh_cooldown=timedelta(seconds=30)
    )
    for _ in range(3):
        with pytest.raises(AuthenticationError):
            check.validate(token(private, kid="missing"))
    assert attempts == 1


def test_last_known_good_survives_bad_refresh_and_rotation_after_cooldown():
    first_private, first_public = keys("first")
    second_private, second_public = keys("second")
    clock = [NOW]
    documents = [{"keys": [first_public]}]
    check = AccessJWTValidator(
        config(),
        fetcher=lambda _: documents[0],
        clock=lambda: clock[0],
        cache_ttl=timedelta(seconds=1),
        refresh_cooldown=timedelta(seconds=1),
    )
    assert check.validate(token(first_private, kid="first")).role == "viewer"
    documents[0] = {"keys": []}
    clock[0] += timedelta(seconds=2)
    assert check.validate(token(first_private, kid="first")).role == "viewer"
    documents[0] = {"keys": [first_public, second_public]}
    clock[0] += timedelta(seconds=2)
    assert check.validate(token(second_private, kid="second")).role == "viewer"
