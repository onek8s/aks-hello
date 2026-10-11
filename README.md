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
src/AksHello/            # ASP.NET Core minimal app: `/` serves the page, `/healthz` the probes
Dockerfile               # SDK build, chiseled non-root ASP.NET Core runtime image
.github/workflows/image.yml   # builds and smoke-tests the image
```

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

## Which cluster answered

The deployment hands the pod the name of its cluster as `CLUSTER_NAME` (aks-fluxcd-example fills it
in from the `cluster_name` Flux post-build variable aks-terraform gives every team), and the page
shows it.
