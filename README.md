# aks-hello

A .NET hello world website - the example application of [OneK8s](https://onek8s.github.io/), one
Kubernetes platform for every cloud. It is deployed by Flux to the `example` team namespace of the
clusters built by [aks-terraform](https://github.com/onek8s/aks-terraform) and published through the
platform's Gateway (Traefik Proxy, Kubernetes Gateway API). The page shows which cluster answered:
https://hello.onek8s.lol is served by every stateless cluster at once, and DNS hands out one or the
other.

This repository holds the app and builds its image. The Kubernetes manifests that deploy it -
`Deployment`, `Service`, `HTTPRoute`, `PodDisruptionBudget` - live in
[onek8s/aks-fluxcd-example](https://github.com/onek8s/aks-fluxcd-example)
(`apps/aks-hello/`), together with the rest of what the `example` team runs; see that repository
for who owns what and how it is applied.

## Layout

```
src/AksHello/            # ASP.NET Core minimal app: `/` the page, `/status` JSON, `/weather` JSON, `/health/*` probes
Dockerfile               # SDK build, chiseled non-root ASP.NET Core runtime image
.github/workflows/image.yml   # builds and smoke-tests the image
```

## The weather forecast (0.4.0)

The page also shows a three-day forecast fetched from the internet, so the app has a real outbound
dependency for the platform's egress control to decide on (OneK8s design section 16): by default
`https://api.open-meteo.com/v1/forecast` (no API key) for Helsinki. `WEATHER_URL` names another URL with
the same response shape (latitude and longitude are part of it), `WEATHER_PLACE` the place shown,
`WEATHER_CACHE_SECONDS` how long a forecast is kept (600), and `WEATHER_DISABLED=true` turns it off. A
failed fetch is shown on the page and at `/weather` with its reason and logged as a warning, retried at
most once a minute per pod; it never fails a probe. From 0.4.1 the forecast is refreshed in the background (every
minute, within the cache time), so the page never waits for the internet and the dependency shows in the
firewall's log even without visitors.

## Running it locally

```sh
dotnet run --project src/AksHello
# or
docker build -t aks-hello . && docker run --rm -p 8080:8080 -e CLUSTER_NAME=local aks-hello
```

## Releasing

The image is built by the environment's Azure Container Registry itself, from this repository at a
pinned commit, and tagged with the `<Version>` in `src/AksHello/AksHello.csproj`. To release:

1. Bump `<Version>` in `src/AksHello/AksHello.csproj` and merge.
2. Add `aks-hello:<version>` with the merge commit to `container_registry_builds` in
   `shared/envs/<environment>.tfvars` of [onek8s/aks-terraform](https://github.com/onek8s/aks-terraform)
   and apply `shared/`: the registry builds the image under that tag.
3. Change the image tag in `apps/aks-hello/deployment.yaml` of
   [onek8s/aks-fluxcd-example](https://github.com/onek8s/aks-fluxcd-example) to the same version and
   merge that. Flux rolls the new tag out on its next sync.

## Access to the image

The registry is private. The clusters pull the image as their kubelet identities, which have
`AcrPull` on it (aks-terraform) - no pull secret. Nothing publishes from this repository, so it
holds no cloud credential; the registry reads it anonymously, as a public repository. The cluster
does not read this repository itself.

## Endpoints

| Path | What |
|---|---|
| `/` | The page: cluster, pod, version |
| `/status` | The same as JSON, plus node (`NODE_NAME`), architecture, start time, uptime and readiness |
| `/health/live` | Liveness: 200 while the process answers HTTP; checks nothing else |
| `/health/ready` | Readiness: 200 from the end of startup until SIGTERM, then 503 |
| `/healthz` | All checks (the probe path before 0.3.0) |

## Which cluster answered

The deployment hands the pod the name of its cluster as `CLUSTER_NAME` (aks-fluxcd-example fills it
in from the `cluster_name` Flux post-build variable aks-terraform gives every team), and the page
shows it.
