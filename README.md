# aks-hello

A .NET hello world website, deployed by Flux to the `example` team namespace of the AKS clusters
built by [aks-terraform](https://github.com/olljanat-ai/aks-terraform) and published through the
platform's Gateway, Traefik Proxy behind an Azure internal load balancer.

This repository holds the app and builds its image. The Kubernetes manifests that deploy it -
`Deployment`, `Service`, `HTTPRoute`, `PodDisruptionBudget` - live in
[olljanat-ai/aks-fluxcd-example](https://github.com/olljanat-ai/aks-fluxcd-example)
(`apps/aks-hello/`), together with the rest of what the `example` team runs; see that repository
for who owns what and how it is applied.

## Layout

```
src/AksHello/            # ASP.NET Core minimal app: `/` serves the page, `/healthz` the probes
Dockerfile               # SDK build, chiseled non-root ASP.NET Core runtime image
.github/workflows/image.yml   # builds the image; publishes it to ACR from main
```

## Running it locally

```sh
dotnet run --project src/AksHello
# or
docker build -t aks-hello . && docker run --rm -p 8080:8080 aks-hello
```

## Releasing

The image is published to `acrsecprototypeshared.azurecr.io/aks-hello`, the environment's Azure
Container Registry, tagged with the `<Version>` in
`src/AksHello/AksHello.csproj`. To release:

1. Bump `<Version>` in `src/AksHello/AksHello.csproj`.
2. Merge. The push to `main` publishes the image.
3. Change the image tag in `apps/aks-hello/deployment.yaml` of
   [olljanat-ai/aks-fluxcd-example](https://github.com/olljanat-ai/aks-fluxcd-example) to the same
   version and merge that. Flux rolls the new tag out on its next sync.

A version that is already in the registry is never overwritten: a merge to `main` that does not
bump it publishes nothing.

## Access to the image

The registry is private. The clusters pull the image as their kubelet identities, which have
`AcrPull` on it (aks-terraform) - no pull secret. Publishing signs in to the registry as the
environment's deployment service principal, which has `AcrPush`: the repository (or organization)
variable `AZURE_CLIENT_ID` and secret `AZURE_CLIENT_SECRET`. The cluster does not read this
repository itself.
