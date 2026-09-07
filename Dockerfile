# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:10 AS builder
WORKDIR /build

# Copy the project files
COPY . .

# Restore packages and publish in Release mode
RUN dotnet publish -c Release \
    --output /app \
    --self-contained=false \
    src/Talent.Mcp.Server/Talent.Mcp.Server.csproj

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10-alpine AS runtime
WORKDIR /app

# Create non-root user
RUN addgroup -g 1001 talent-mcp && \
    adduser -D -u 1001 -G talent-mcp talent-mcp

# Copy published app from builder
COPY --from=builder /app .

# Change ownership to the non-root user
RUN chown -R talent-mcp:talent-mcp /app

# Switch to non-root user
USER talent-mcp

# Expose ports: 5000 (HTTP), 5001 (HTTPS)
EXPOSE 5000 5001

# Healthcheck: ensure the server is responding
# Requires curl; alternatively use dotnet built-in endpoints
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
    CMD wget -q -O - http://localhost:5000/health || exit 1

# Set environment for ASP.NET Core
ENV ASPNETCORE_URLS=http://+:5000
ENV ASPNETCORE_ENVIRONMENT=Production

# Run the MCP server
ENTRYPOINT ["dotnet", "Talent.Mcp.Server.dll"]
