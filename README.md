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
src/AksHello/            # ASP.NET Core minimal app: `/` the page, `/status`, `/weather` and `/public-ip` JSON, `/health/*` probes
Dockerfile               # SDK build, non-root Ubuntu ASP.NET Core runtime image with a shell
.github/workflows/image.yml   # builds and smoke-tests the image
```

## A shell for debugging (0.6.0)

The runtime image is `mcr.microsoft.com/dotnet/aspnet:10.0-noble` (Ubuntu 24.04) instead of the chiseled one,
so `kubectl exec -it <pod> -- bash` works for debugging; it still runs as the non-root user 1654 on a
read-only root file system. There is no curl or other network tool in it: add one only to a debug container
(`kubectl debug`).

## The weather forecast (0.4.0)

The page also shows a three-day forecast fetched from the internet, so the app has a real outbound
dependency for the platform's egress control to decide on (OneK8s design section 5.1): by default
`https://api.open-meteo.com/v1/forecast` (no API key) for Helsinki. `WEATHER_URL` names another URL with
the same response shape (latitude and longitude are part of it), `WEATHER_PLACE` the place shown,
`WEATHER_CACHE_SECONDS` how long a forecast is kept (600), and `WEATHER_DISABLED=true` turns it off. A
failed fetch is shown on the page and at `/weather` with its reason and logged as a warning, retried at
most once a minute per pod; it never fails a probe. From 0.4.1 the forecast is refreshed in the background (every
minute, within the cache time), so the page never waits for the internet and the dependency shows in the
firewall's log even without visitors.

## Which HTTP version reached the pod (0.5.1), and the h2c port (0.5.2)

`/status` reports `protocol`: `HTTP/2` when the caller connected over HTTP/2 cleartext with prior knowledge
(h2c), `HTTP/1.1` otherwise. Since 0.5.2 the app listens on two ports: **8080 HTTP/1.1** (the kubelet's probes,
which speak HTTP/1.1 until Kubernetes 1.38) and **8081 HTTP/2 only (h2c)**, the port a Service with
`appProtocol: kubernetes.io/h2c` targets. Kestrel's default (`Http1AndHttp2`) is HTTP/1.1 alone on a port
without TLS, because there is no ALPN to agree on HTTP/2 - 0.5.1 relied on it and every route behind Traefik
answered 500. The OneK8s platform connects its gateways to pods over h2c only (design D69, "HTTP/1.1 must die":
no HTTP/1.1 between a proxy and its back ends), so through a zone gateway the field reads `HTTP/2`. Try it:
`curl --http2-prior-knowledge http://localhost:8081/status` and `curl http://localhost:8080/status`.

## The node's public IP (0.5.0)

The page and `/status` (`nodePublicIp`) also show the public IP the pod reaches the internet from, as an
"echo my address" service sees it: by default `https://api.ipify.org` (no API key). It is what tells a
visitor which address the platform's egress - a NAT gateway, an egress gateway, or the node itself - gave
this pod, and a second outbound dependency for the egress control. It is checked once, when the pod
starts (up to three attempts five seconds apart, for an egress that is not ready the moment the pod is),
and kept in memory for the life of the pod: nothing asks again, and a pod that found nothing shows the
reason on the page and at `/public-ip` until it is restarted. The check runs in the background, so the
app is ready without waiting for it. `PUBLIC_IP_URL` names another URL that answers a plain-text IP
address, and `PUBLIC_IP_DISABLED=true` turns it off. It never fails a probe.

## Running it locally

```sh
dotnet run --project src/AksHello
# or
docker build -t aks-hello . && docker run --rm -p 8080:8080 -p 8081:8081 -e CLUSTER_NAME=local aks-hello
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
| `/` | The page: cluster, pod, version, the node's public IP, the weather forecast |
| `/status` | The same as JSON, plus node (`NODE_NAME`), architecture, the HTTP protocol the caller spoke (`protocol`, 0.5.1), start time, uptime and readiness |
| `/weather` | The forecast as JSON, or why there is none |
| `/public-ip` | The node's public IP found when the pod started, or why none was |
| `/health/live` | Liveness: 200 while the process answers HTTP; checks nothing else |
| `/health/ready` | Readiness: 200 from the end of startup until SIGTERM, then 503 |
| `/healthz` | All checks (the probe path before 0.3.0) |

## Which cluster answered

The deployment hands the pod the name of its cluster as `CLUSTER_NAME` (aks-fluxcd-example fills it
in from the `cluster_name` Flux post-build variable aks-terraform gives every team), and the page
shows it.
