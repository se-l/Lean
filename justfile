set shell := ["bash", "-c"]
set windows-shell := ["pwsh", "-c"]

registry := "sebastianluen"
tag := "dev"

# Build an image: just build lean
build-amm tag="dev":
    docker buildx build \
        -f Dockerfile-amm \
        -t {{registry}}/amm:{{tag}} \
        .
    @echo "Build completed: amm"
 
build-lean tag="latest":
    docker buildx build \
        -f Dockerfile-lean \
        -t {{registry}}/lean:{{tag}} \
        .
    @echo "Build completed: lean"

build-toolbox tag="prd":
    docker buildx build \
        -f Dockerfile-toolbox \
        --platform linux/amd64,linux/arm64 \
        -t {{registry}}/amm:toolbox.{{tag}} \
        --push \
        .
    @echo "Build completed: toolbox"

# One-time setup: create the multi-arch builder
setup-builder:
    #!/usr/bin/env bash
    if docker buildx inspect multiarch &>/dev/null; then
        docker buildx use multiarch
        echo "Builder 'multiarch' already exists, selected"
    else
        docker buildx create --name multiarch --driver docker-container --use
        echo "Builder 'multiarch' created and set as default"
    fi
    docker buildx inspect --bootstrap


# Tag with version
tag name tagfrom tagto:
    docker tag {{registry}}/{{name}}:{{tagfrom}} {{registry}}/{{name}}:{{tagto}}

# Push: just push mind
push name tag="dev":
    docker push {{registry}}/{{name}}:{{tag}}
