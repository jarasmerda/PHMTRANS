# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore
# .git is not in the build context. Pass --build-arg GIT_BRANCH=name for /health.
ARG GIT_BRANCH=
RUN dotnet publish -c Release -o /app/publish -p:GitBranch="${GIT_BRANCH}"

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "PHMTRANS.dll"]