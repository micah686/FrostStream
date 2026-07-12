FROM openfga/openfga:v1.18.0 AS openfga

FROM docker.io/library/postgres:18.3
COPY --from=openfga /openfga /usr/local/bin/openfga
ENTRYPOINT ["/bin/bash", "-c"]
