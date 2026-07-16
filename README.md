# Jarvis

Standalone monitoring dashboard for the 14 independent microservices, across every Azure Web
App Service environment each module is deployed to. Lives entirely outside every existing module
folder — nothing in `APi_Gateway/`, `Export/`, etc. is touched or built against; it only makes
outbound HTTP calls to their already-existing `verifyapi/check` / `verifyapi/checksql` endpoints
and to each environment's shared Shell-UI origin.

## Feature set

Beyond the core uptime status board, Jarvis includes features ported from the Sentinel project:

- **RBAC** — per-user accounts with `admin` / `developer` roles. Admins manage users and see the
  audit log; developers get read-only access to the dashboards. A default admin is seeded on first
  boot from the `BasicAuth` credential (zero-setup).
- **User management** — `Users` tab (admin only): list, create, and delete accounts.
- **Activity / audit log** — `Activity` tab (admin only): who logged in and who changed what, with
  IP and timestamp.
- **Azure DevOps pipeline dashboard** — `Pipelines` tab: build + release pipeline statuses and
  failed-task errors. Configure via `AZURE_DEVOPS_ORG` / `AZURE_DEVOPS_PROJECT` / `AZURE_DEVOPS_PAT`
  (leave blank to disable).
- **Ocelot auto-importer** — drop an Ocelot gateway JSON into `ocelot/` and Jarvis auto-creates
  monitored targets from its `verifyapi/check` routes, grouped into an environment by filename
  (e.g. `Ocelot.TestingD.JSON` → `DEV`).

## Run locally

```
cd Monitoring
docker compose up --build
```

- Dashboard: http://localhost:8081 (Operator ID `devops`, passcode `Cargo-Watch-2026!` — local test only)
- Raw API: http://localhost:8080/api/status?environment=QA

Polls every 60s, across all configured environments simultaneously. History is kept for 7 days
(rolling) in the `monitoring-db` Postgres container.

## Configuration

- `Monitoring_API/config/targets.json` — single file, all environments nested under
  `Monitoring:Environments`. Add a new environment by appending an entry (name, shared UI base
  URL, and its list of module targets) — no code change needed.
- Currently configured: `QA`, `DEV`, `PT`, `POST_GRE`, `POST_GRE_VAPT`, `UAT_CES`, `UAT_ACS`,
  `UAT_ACS_IGA`, `UAT_MPA`, `PROD_BUD`, `PROD_CES`, `PROD_ACS`, `PROD_BOG`, `PROD_LPMS` — every
  environment across all 14 modules with `deploymentType: webapp` in their `pipelines/deploy-matrix.yml`
  (IIS deployment-group environments like UAT_OMAN, PROD_OMAN, UAT_LCCT are intentionally excluded —
  different monitoring approach needed for those).
- Basic auth defaults to `devops` / `Cargo-Watch-2026!` for local verification. Override via
  `MONITORING_BASIC_AUTH_USER` / `MONITORING_BASIC_AUTH_PASSWORD` — change these before this ever
  runs anywhere shared.

## Confidence level by environment

- **QA hostnames are verified**: cross-checked directly against `APi_Gateway/API_Gateway/Ocelot.QA.json`
  (the gateway's live routing table) and confirmed reachable.
- **All other environments' hostnames are inferred**: `pipelines/deploy-matrix.yml` gives each
  module's Azure Web App *name* per environment, not its full hostname — this config assumes the
  standard `<name>.azurewebsites.net` pattern, which is NOT independently confirmed the way QA was.
  A first local run already surfaced real DNS failures on a few inferred hostnames (e.g. some
  PCS/EDI/ContentMgt/Hospitality entries in PROD_ACS, PROD_CES, PROD_BOG, POST_GRE, POST_GRE_VAPT)
  — these may mean the environment is genuinely down, or the hostname guess is wrong. Worth a
  quick sanity pass with the infra team before treating those as real incidents.
- Two entries were deliberately excluded due to data-quality issues found in the source repo
  during research: Export's `POST_GRE` deploy-matrix entry (looks like a copy-paste leftover
  referencing EDI), and APi_Gateway's `UAT_MPA` entry (uses a mismatched field name vs. every
  other `webapp`-type entry).

## Known naming outliers (QA)

`EDI` and `Hospitality` don't follow the `acsint<module>api` hostname convention the other 12
modules use in QA — confirmed against `Ocelot.QA.json`, not a bug in this tool.

## Next steps (not in this v1)

- Verify/correct the inferred non-QA hostnames flagged above.
- Deploy the same containers to the self-hosted agent once this local verification is signed off.
- Add email/Teams alerting on state transitions.
- Consider a lighter polling cadence for PROD environments specifically, since this now polls
  ~150 targets across 14 environments every 60 seconds, continuously, against real infrastructure.
