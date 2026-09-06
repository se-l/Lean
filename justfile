set shell := ["bash", "-c"]
set windows-shell := ["pwsh", "-c"]

registry := "sebastianluen"
tag := "latest"

# Build an image: just build vastmind/vastmindbase/zmqpricer prd/mind dev
build name="" tag="latest":
    docker buildx build \
        -f Dockerfile-{{name}} \
        -t {{registry}}/{{name}}:{{tag}} \
        .
    @echo "Build completed: {{name}}"

# Tag with version
tag name tagfrom tagto:
    docker tag {{registry}}/{{name}}:{{tagfrom}} {{registry}}/{{name}}:{{tagto}}

# Push: just push mind
push name tag="latest":
    docker push {{registry}}/{{name}}:{{tag}}
