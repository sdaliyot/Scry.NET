# Security policy

## Supported versions

Only the latest published release is supported. There is no LTS branch; fixes land on `main` and go
out in the next release.

## Reporting a vulnerability

Report a suspected vulnerability privately through GitHub's [private vulnerability
reporting](https://github.com/sdaliyot/Scry.NET/security/advisories/new) - open the repository's
"Security" tab and choose "Report a vulnerability" - rather than a public issue. This creates a
private advisory visible only to you and the maintainer, so details of an unpatched issue aren't
public before a fix ships.

Please include:
- The Scry.NET version or commit you're using.
- Whether it's an attach-mode or embedded-mode target, and which runtime (.NET Framework or modern
  .NET).
- Steps to reproduce, and what you'd expect the trust boundary to have prevented.

## What Scry.NET is, for context

Scry.NET is local-only developer/test tooling, not a deployed network service - see
[`docs/threat-model.md`](docs/threat-model.md) for the full trust-boundary discussion, including what
is and is not in scope (for example: deliberate code execution and state mutation inside an attached
process is the *product*, not a vulnerability - the threat model covers what boundary that's meant to
respect and where it's opt-in).
