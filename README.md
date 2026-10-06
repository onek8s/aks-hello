# aks-hello

A .NET hello world website, deployed by Flux to the `example` team namespace of the AKS clusters
built by [aks-terraform](https://github.com/olljanat-ai/aks-terraform) and published to the
internet through the platform's Gateway on Application Gateway for Containers.

## Layout

```
src/AksHello/            # ASP.NET Core minimal app: `/` serves the page, `/healthz` the probes
Dockerfile               # SDK build, chiseled non-root ASP.NET Core runtime image
deploy/                  # what Flux applies to the `example` namespace
  deployment.yaml
  service.yaml
  httproute.yaml         # attaches to the platform's `public` Gateway
  poddisruptionbudget.yaml
.github/workflows/image.yml   # builds the image; publishes it to GHCR from main
```

## Who owns what

The split follows the [Gateway API role model][roles]: the platform runs the gateway, the team
routes to its app.

| | Where | |
| --- | --- | --- |
| `example` namespace, its quota, network policy and Pod Security Standard | aks-terraform | Platform |
| The Gateway API itself: managed CRDs, the Application Gateway for Containers add-on and its subnet | aks-terraform | Platform |
| The Application Gateway for Containers load balancer, the `public` Gateway, its hostnames and listeners | aks-fluxcd | Platform |
| TLS: cert-manager, the Let's Encrypt issuer, the certificate of each listener | aks-fluxcd | Platform |
| HTTP to HTTPS redirect | aks-fluxcd | Platform |
| The Flux `GitRepository` and `Kustomization` that apply `deploy/`, what they may do, and the `NetworkPolicy` that lets the load balancer reach the pods | aks-fluxcd (`tenants/example`) | Platform |
| The app, its image, `Deployment`, `Service`, `HTTPRoute` | this repository | Team |

The `HTTPRoute` names no hostname and no certificate. It attaches to the `https-example` listener of
the `public` Gateway in `ingress-gateway`, which the platform restricts to routes from the
`example` namespace, and takes its hostname from there. Changing the hostname or the certificate
is a change to aks-fluxcd; nothing here moves.

Flux applies `deploy/` as the `flux-reconciler` service account of the `example` namespace, which
holds the `admin` role in that namespace and nothing outside it - so this repository can only ever
change the `example` namespace.

[roles]: https://gateway-api.sigs.k8s.io/concepts/roles-and-personas/

The hostname is set per cluster in aks-fluxcd (`clusters/<cluster>/cluster-settings.yaml`).

## Running it locally

```sh
dotnet run --project src/AksHello
# or
docker build -t aks-hello . && docker run --rm -p 8080:8080 aks-hello
```

## Releasing

The image is published to `ghcr.io/olljanat-ai/aks-hello`, tagged with the `<Version>` in
`src/AksHello/AksHello.csproj`. To release:

1. Bump `<Version>` in `src/AksHello/AksHello.csproj`.
2. Change the image tag in `deploy/deployment.yaml` to the same version. The workflow fails a pull
   request where the two disagree.
3. Merge. The push to `main` publishes the image, and Flux rolls the new tag out on its next sync.

A version that is already in the registry is never overwritten: a merge to `main` that does not
bump it publishes nothing.

## Access to this private repository and its image

This repository and its image package are private, so the cluster needs two secrets in the
`example` namespace. Neither is in Git:

- **`aks-hello-git`** - read access to this repository, for Flux. A fine-grained personal access
  token (or a GitHub App) with read-only `Contents` on `olljanat-ai/aks-hello`:

  ```sh
  kubectl -n example create secret generic aks-hello-git \
    --from-literal=username=git --from-literal=password=github_pat_...
  ```

- **`ghcr-pull`** - read access to the image, for the nodes. A token with `read:packages`:

  ```sh
  kubectl -n example create secret docker-registry ghcr-pull \
    --docker-server=ghcr.io --docker-username=<github user> --docker-password=<token>
  ```

  Not needed once the package is made public (package settings on GitHub); the reference in
  `deploy/deployment.yaml` is harmless without the secret.
