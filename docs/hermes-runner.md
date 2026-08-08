# Verified Hermes Copilot runner contract

Verified locally on 2026-08-06 with `/opt/hermes/bin/hermes`.

All four locked model IDs accepted by the Copilot provider and returned the requested JSON:

- `gpt-5.6-sol`
- `claude-opus-4.8`
- `claude-sonnet-5`
- `gemini-3.1-pro-preview`

Programmatic invocation:

```bash
/opt/hermes/bin/hermes -z "$PROMPT" \
  -m "$MODEL_ID" \
  --provider copilot \
  -t web \
  --safe-mode
```

`-z` prints only the final response, unlike `hermes chat -Q`, which appends a `session_id` footer. The application must:

- Pass arguments without a shell and impose a timeout.
- Use Hermes safe mode rather than merely ignoring rules. Safe mode is required to prevent user
  configuration, fallback chains, hooks, plugins, MCP servers, and middleware from extending the
  approved invocation.
- Sanitize the child environment to the credential-home and TLS-certificate paths needed by Hermes;
  do not inherit hook, plugin, Python-path, proxy, or shell configuration.
- Kill and boundedly drain the complete process group. A child inheriting stdout/stderr must not keep
  capture alive after the Hermes leader exits.
- Allowlist the four exact model IDs; never substitute or use fallback models.
- Use isolated subprocesses and prompts containing only that model's own state.
- Require strict JSON output and validate it before any order call.
- Treat non-zero exit, timeout, malformed JSON, or wrong model ID as a missed run.
- Keep secrets in the existing Hermes credential store; never copy tokens into this application.
- Store the provider (`copilot`), model ID, prompt-contract version, decision time, raw final response, and validation result.

The runner caps each output stream at 65,536 bytes and the complete serialized prompt at 100,000
UTF-8 bytes. Decision text, evidence arrays, risks, strategy updates, IDs, and URLs also have explicit
bounds. Evidence URLs require exact public DNS hostnames over HTTPS without credentials or ports.

A one-shot `-t web` probe returned fail-closed JSON rather than fabricating an official URL. External-research quality and tool availability therefore remain part of the full dry-run launch gate.
