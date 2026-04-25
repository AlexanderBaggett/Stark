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
compresses responses, and sets conservative security/cache headers.
