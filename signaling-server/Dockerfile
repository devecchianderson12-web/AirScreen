FROM golang:1.24-alpine AS build
WORKDIR /src
COPY go.mod go.sum ./
RUN go mod download
COPY *.go ./
RUN CGO_ENABLED=0 go build -trimpath -ldflags="-s -w" -o /dualscreen-signaling .

FROM gcr.io/distroless/static:nonroot
COPY --from=build /dualscreen-signaling /dualscreen-signaling
EXPOSE 8443
USER nonroot
ENTRYPOINT ["/dualscreen-signaling"]
