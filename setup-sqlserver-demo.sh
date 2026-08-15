#!/usr/bin/env bash
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

echo "========================================================"
echo "🚀 SSIS Lineage Hub — SQL Server Demo DB Setup"
echo "========================================================"

# Determine docker command (docker vs sudo docker)
DOCKER_CMD="docker"
if ! docker info >/dev/null 2>&1; then
    if command -v sudo &>/dev/null && sudo docker info >/dev/null 2>&1; then
        DOCKER_CMD="sudo docker"
    else
        echo "❌ Cannot connect to Docker daemon."
        echo "Please ensure Docker is installed and running."
        exit 1
    fi
fi

# Determine compose command
if $DOCKER_CMD compose version >/dev/null 2>&1; then
    COMPOSE_CMD="$DOCKER_CMD compose"
elif command -v docker-compose &>/dev/null; then
    COMPOSE_CMD="docker-compose"
else
    COMPOSE_CMD="$DOCKER_CMD compose"
fi

echo "📦 Starting SQL Server 2022 container..."
$COMPOSE_CMD -f docker-compose.demo-db.yml up -d

echo "⏳ Waiting for SQL Server to start..."
MAX_RETRIES=30
RETRY_COUNT=0

until $DOCKER_CMD exec ssis-demo-sqlserver /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P 'YourPassword123!' -C -Q "SELECT 1" >/dev/null 2>&1 || \
      $DOCKER_CMD exec ssis-demo-sqlserver /opt/mssql-tools/bin/sqlcmd -S localhost -U sa -P 'YourPassword123!' -Q "SELECT 1" >/dev/null 2>&1; do
    RETRY_COUNT=$((RETRY_COUNT+1))
    if [ $RETRY_COUNT -ge $MAX_RETRIES ]; then
        echo "❌ Timed out waiting for SQL Server to accept connections."
        echo "Run '$DOCKER_CMD logs ssis-demo-sqlserver' to inspect the container logs."
        exit 1
    fi
    echo "   ... waiting ($RETRY_COUNT/$MAX_RETRIES)"
    sleep 2
done

echo "========================================================"
echo "✅ SQL Server Container is READY!"
echo ""
echo "Host: localhost,1433 | User: sa"
echo "========================================================"
