# Security Policy

## Supported versions

Stark has not yet published its first stable release. Security fixes currently
target the default branch and the newest public release candidate, when one
exists. After stable releases begin, only the newest stable release line and the
default branch are supported unless a release explicitly states otherwise.

| Version | Security support |
|---|---|
| Default branch | Best effort during pre-release development |
| Newest published release/candidate | Supported when published |
| Older releases/candidates | Not supported unless explicitly announced |

## Reporting a vulnerability

Use GitHub's **Report a vulnerability** / private security-advisory flow for
this repository. Include the affected commit or release, target platform,
reproduction steps, impact, and any proposed mitigation. Do not disclose an
unpatched vulnerability in a public issue, discussion, pull request, or build
log.

If private vulnerability reporting is unavailable, open a public issue asking
the maintainers to establish a private contact channel without including
security-sensitive details.

The maintainers will acknowledge receipt when practical, investigate the report,
and coordinate disclosure and release timing according to severity and project
capacity. The project does not currently promise a fixed response-time SLA.

Security-sensitive release inputs—including signing keys, notarization
credentials, repository tokens, and package provenance credentials—must use
GitHub environments/secrets or the platform's approved secret store and must
never be committed to the repository or embedded in release archives.
