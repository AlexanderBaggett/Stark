# Stark Website Stack

The Stark documentation website uses Hugo for static generation and Caddy for serving the generated output.

This stack is intentionally small:

- Hugo builds Markdown documentation into static files without requiring npm or Python.
- Caddy serves the generated `public/` directory directly and handles HTTPS, redirects, compression, and cache headers at the edge of the site.
- The generated site remains deployable with ordinary file synchronization over SSH.

The website build keeps its tooling pinned and local to the repository. The intended shape is:

- a pinned Hugo binary at `tools/hugo/hugo`;
- a pinned Hugo version in `tools/hugo/VERSION`;
- a build script at `scripts/build-site.sh` that verifies the pinned version before rendering;
- a Hugo config and content tree under `site/`;
- generated output in Hugo's `public/` directory;
- a Caddy configuration at `deploy/Caddyfile` that serves only the generated output;
- a deployment script at `scripts/deploy-site.sh` that builds the site and syncs `site/public/` over SSH with `rsync`.
- a link checker at `scripts/check-site-links.sh` that validates internal
  `href` and `src` targets in generated HTML.

The initial scaffold is deliberately small. Repository Markdown files remain the source of truth for language docs, examples, roadmap, benchmark notes, and standard library documentation until those pages are intentionally published into the Hugo content tree.

`scripts/deploy-site.sh` requires these environment variables:

- `STARK_SITE_HOST`
- `STARK_SITE_USER`
- `STARK_SITE_REMOTE_DIR`

`STARK_SITE_SSH_PORT` is optional and defaults to `22`.

`deploy/Caddyfile` expects these environment variables on the server:

- `STARK_SITE_DOMAIN`
- `STARK_SITE_ACME_EMAIL`

`STARK_SITE_ROOT` is optional and defaults to `/var/www/stark/public`.

The Caddyfile enables HTTPS through Caddy's normal certificate automation,
redirects `www.<domain>` to the apex domain, serves prebuilt static files,
compresses responses, writes rotating access logs, and sets conservative
security/cache headers.

## Local Verification

Before deploying, run the same checks CI should run:

```bash
scripts/build-site.sh
scripts/check-site-links.sh
```

`scripts/build-site.sh` regenerates rendered reference pages, exports the book,
and writes the static site to `site/public/`. `scripts/check-site-links.sh`
checks local `href` and `src` targets, ignores external links, and catches
regressions where code samples leak escaped quote entities.

## SSH Deployment

The deployment path is intentionally plain `rsync` over SSH. The script builds
the site first, then synchronizes only generated static output:

```bash
export STARK_SITE_HOST=example.org
export STARK_SITE_USER=stark-deploy
export STARK_SITE_REMOTE_DIR=/var/www/stark/public
export STARK_SITE_SSH_PORT=22

scripts/deploy-site.sh
```

The remote directory should be owned by the deploy user or by a group the deploy
user can write to. Caddy only needs read access. A typical layout is:

```text
/var/www/stark/
  public/     # rsync target
  backups/    # optional local restore point storage
```

The script uses `--delete`, so files removed from the generated site are removed
from the server on the next deploy. Keep source Markdown and generated content
in version control, not only on the server.

## Server Hardening Baseline

The production VPS should be configured before the first public deploy:

- use a dedicated non-root deploy account such as `stark-deploy`
- allow SSH key authentication only
- disable password SSH login and direct root SSH login
- restrict firewall ingress to SSH, HTTP, and HTTPS
- keep the Caddy service running as its own service user
- keep `site/public/` writable by the deploy account and readable by Caddy
- enable automatic security updates through the distribution's package manager
- retain Caddy/system logs for enough time to debug deploy and TLS issues

For Debian or Ubuntu style hosts, the SSH daemon settings usually live under
`/etc/ssh/sshd_config` or `/etc/ssh/sshd_config.d/*.conf`:

```text
PasswordAuthentication no
PermitRootLogin no
PubkeyAuthentication yes
```

Firewall policy should allow only the selected SSH port plus `80/tcp` and
`443/tcp`. The exact tool can be `ufw`, `firewalld`, nftables, or the provider
firewall; the important part is that HTTP and HTTPS reach Caddy while unrelated
services stay closed.

Caddy access logs are written to `STARK_SITE_ACCESS_LOG`, defaulting to
`/var/log/caddy/stark-access.log`, with size and age rotation configured in the
repository Caddyfile. System logs should also have journal or logrotate retention
configured by the host operating system.

## Backup And Restore

The canonical source for the website is the repository: Markdown, layouts,
theme assets, Hugo config, scripts, and `deploy/Caddyfile`. The server should be
treated as rebuildable.

Back up these items:

- repository state, including the commit used for the current deploy
- server environment file or service drop-in containing `STARK_SITE_DOMAIN`,
  `STARK_SITE_ACME_EMAIL`, `STARK_SITE_ROOT`, and optional log path settings
- active `deploy/Caddyfile` copy on the server
- `/var/www/stark/public/` if a quick rollback of generated output is useful
- Caddy data and config directories if preserving existing TLS account state is
  important for fast recovery

A simple generated-output backup before deployment is:

```bash
timestamp="$(date -u +%Y%m%dT%H%M%SZ)"
tar -C /var/www/stark -czf "/var/www/stark/backups/public-${timestamp}.tar.gz" public
```

Restore generated output from a backup with:

```bash
systemctl stop caddy
rm -rf /var/www/stark/public
tar -C /var/www/stark -xzf /var/www/stark/backups/public-YYYYMMDDTHHMMSSZ.tar.gz
systemctl start caddy
```

Restore from source by cloning the repository at the known-good commit, applying
the server environment, running `scripts/build-site.sh`, and deploying with
`scripts/deploy-site.sh`. After either restore path, check:

```bash
caddy validate --config /etc/caddy/Caddyfile
systemctl status caddy
curl -I "https://${STARK_SITE_DOMAIN}/"
```
