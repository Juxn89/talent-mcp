#!/bin/bash
# Pre-tag verification script for F5 (Packaging & CI)
# Run this before: git tag v1.0.0 && git push origin v1.0.0

set -euo pipefail

SKIP_TESTS=false
SKIP_COMPOSE=false
BUILD_DOCKER=false

while [[ $# -gt 0 ]]; do
    case $1 in
        --skip-tests) SKIP_TESTS=true; shift ;;
        --skip-compose) SKIP_COMPOSE=true; shift ;;
        --docker) BUILD_DOCKER=true; shift ;;
        *) echo "Unknown flag: $1"; exit 1 ;;
    esac
done

ALL_PASSED=true
TEMP_NUPKG_DIR=$(mktemp -d)

function section() {
    echo ""
    echo "════════════════════════════════════════════════════════════"
    echo "$(tput bold)$(tput setaf 6)$1$(tput sgr0)"
    echo "════════════════════════════════════════════════════════════"
}

function success() {
    echo "$(tput setaf 2)✅ $1$(tput sgr0)"
}

function error_msg() {
    echo "$(tput setaf 1)❌ $1$(tput sgr0)"
}

function info_msg() {
    echo "$(tput setaf 3)ℹ️  $1$(tput sgr0)"
}

trap 'cleanup' EXIT

function cleanup() {
    if [[ "$SKIP_COMPOSE" != "true" ]] && command -v docker &> /dev/null; then
        info_msg "Stopping compose stack..."
        docker compose -f deploy/compose.yaml down 2>/dev/null || true
    fi
    rm -rf "$TEMP_NUPKG_DIR" || true
}

# Step 1: Check dependencies
section "Step 1: Verify Prerequisites"

DOTNET_VERSION=$(dotnet --version)
success "dotnet: $DOTNET_VERSION"

if command -v docker &> /dev/null; then
    DOCKER_VERSION=$(docker --version)
    success "docker: $DOCKER_VERSION"
else
    info_msg "docker not available (will skip Dockerfile build)"
fi

# Step 2: Build Release
section "Step 2: Build (Release, no warnings)"

if dotnet build -c Release > /tmp/build.log 2>&1; then
    if grep -i "warning" /tmp/build.log > /dev/null; then
        error_msg "Build succeeded but found warnings:"
        grep "warning" /tmp/build.log || true
        ALL_PASSED=false
    else
        success "Build succeeded with 0 warnings"
    fi
else
    error_msg "Build failed"
    tail -20 /tmp/build.log
    ALL_PASSED=false
fi

# Step 3: Run tests
if [[ "$SKIP_TESTS" != "true" ]]; then
    section "Step 3: Run All Tests (5 levels)"

    if [[ "$SKIP_COMPOSE" != "true" ]] && command -v docker &> /dev/null; then
        info_msg "Starting docker compose stack..."
        if docker compose -f deploy/compose.yaml up -d --wait 2>/dev/null; then
            success "Compose stack started"
        else
            info_msg "Compose stack not available (skipping conformance + E2E tests)"
            SKIP_COMPOSE=true
        fi
    fi

    declare -a TEST_PROJECTS=(
        "tests/Talent.Architecture.Tests|Architecture (dependency rule)"
        "tests/Talent.Domain.Tests|Domain (pure functions)"
        "tests/Talent.Infrastructure.Tests|Infrastructure (EF mapping)"
        "tests/Talent.Mcp.Tests|Tools (in-memory transport)"
        "tests/Talent.Mcp.Conformance|Conformance (protocol spec)"
        "tests/Talent.Mcp.E2E|E2E (full stack)"
    )

    for test_pair in "${TEST_PROJECTS[@]}"; do
        IFS='|' read -r PROJECT TEST_NAME <<< "$test_pair"

        if [[ "$TEST_NAME" =~ (Conformance|E2E) ]] && [[ "$SKIP_COMPOSE" == "true" ]]; then
            info_msg "Skipping $TEST_NAME (compose not available)"
            continue
        fi

        echo "Running $TEST_NAME..."
        if dotnet test "$PROJECT" -c Release --no-build > /tmp/test.log 2>&1; then
            success "$TEST_NAME passed"
        else
            error_msg "$TEST_NAME failed"
            tail -20 /tmp/test.log
            ALL_PASSED=false
        fi
    done
else
    info_msg "Skipping tests (--skip-tests flag)"
fi

# Step 4: Pack packages
section "Step 4: Pack NuGet Packages"

declare -a PACKAGES=(
    "src/Talent.Mcp.Server.Stdio|Talent.Mcp.Server (dotnet tool)"
    "src/Talent.Mcp.Toolkit|Talent.Mcp.Toolkit (library)"
)

for pkg_pair in "${PACKAGES[@]}"; do
    IFS='|' read -r PROJECT PKG_NAME <<< "$pkg_pair"

    echo "Packing $PKG_NAME..."
    if dotnet pack "$PROJECT" -c Release --no-build --output "$TEMP_NUPKG_DIR" > /tmp/pack.log 2>&1; then
        NUPKG=$(ls "$TEMP_NUPKG_DIR"/*.nupkg 2>/dev/null | head -1 || echo "")
        if [[ -n "$NUPKG" ]]; then
            success "$PKG_NAME → $(basename "$NUPKG")"
        else
            error_msg "No .nupkg file found for $PKG_NAME"
            ALL_PASSED=false
        fi
    else
        error_msg "$PKG_NAME pack failed"
        tail -20 /tmp/pack.log
        ALL_PASSED=false
    fi
done

# Step 5: Verify package structure
section "Step 5: Verify Package Contents"

SERVER_NUPKG=$(ls "$TEMP_NUPKG_DIR"/Talent.Mcp.Server*.nupkg 2>/dev/null | head -1 || echo "")
if [[ -n "$SERVER_NUPKG" ]]; then
    echo "Examining $(basename "$SERVER_NUPKG")..."
    TEMP_UNZIP=$(mktemp -d)
    unzip -q "$SERVER_NUPKG" -d "$TEMP_UNZIP" 2>/dev/null || true

    if [[ -d "$TEMP_UNZIP/tools" ]]; then
        success "dotnet tool has tools/ folder (✓ valid tool package)"
    else
        error_msg "dotnet tool missing tools/ folder"
        ALL_PASSED=false
    fi

    rm -rf "$TEMP_UNZIP"
fi

# Step 6: Docker image (optional)
if [[ "$BUILD_DOCKER" == "true" ]] && command -v docker &> /dev/null; then
    section "Step 6: Build Docker Image"

    echo "Building docker image..."
    if docker build -t talent-mcp:verify . > /tmp/docker.log 2>&1; then
        success "Docker image built: talent-mcp:verify"

        USER_CHECK=$(docker inspect --format='{{.Config.User}}' talent-mcp:verify 2>/dev/null || echo "")
        if [[ -n "$USER_CHECK" ]] && [[ "$USER_CHECK" != "root" ]] && [[ "$USER_CHECK" != "0" ]]; then
            success "Image runs as non-root user: $USER_CHECK"
        else
            info_msg "Image user: $USER_CHECK (expected non-root for security)"
        fi
    else
        error_msg "Docker build failed"
        tail -20 /tmp/docker.log
        ALL_PASSED=false
    fi
else
    info_msg "Skipping Docker build (--docker flag not set)"
fi

# Step 7: Summary
section "Verification Summary"

if [[ "$ALL_PASSED" == "true" ]]; then
    success "All checks passed! ✅"
    echo ""
    echo "$(tput bold)$(tput setaf 2)Next steps to publish:$(tput sgr0)"
    echo "  1. git push origin feat/f5-packaging-ci (open PR and wait for CI)"
    echo "  2. After CI passes, merge PR to main"
    echo "  3. Tag and publish: git tag v1.0.0 && git push origin v1.0.0"
    echo ""
    exit 0
else
    error_msg "Some checks failed. Fix errors above and re-run."
    echo ""
    exit 1
fi
