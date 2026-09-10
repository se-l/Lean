set shell := ["bash", "-c"]
set windows-shell := ["pwsh", "-c"]

registry := "sebastianluen"
tag := "latest"

# Build an image: just build vastmind/vastmindbase/zmqpricer prd/mind dev
build name="" tag="latest":
    docker buildx build \
        -f Dockerfile-{{name}} \
        --platform linux/amd64,linux/arm64 \
        -t {{registry}}/{{name}}:{{tag}} \
        --push \
        .
    @echo "Build completed: {{name}}"

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
push name tag="latest":
    docker push {{registry}}/{{name}}:{{tag}}
