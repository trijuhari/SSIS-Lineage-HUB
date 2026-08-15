#!/usr/bin/env bash
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

# Exit immediately if a command exits with a non-zero status
set -e

# Color codes for pretty printing
GREEN='\033[0;32m'
BLUE='\033[0;34m'
YELLOW='\033[1;33m'
RED='\033[0;31m'
NC='\033[0m' # No Color

echo -e "${BLUE}==============================================${NC}"
echo -e "${BLUE}   SSIS Lineage Portal Startup Script        ${NC}"
echo -e "${BLUE}==============================================${NC}"

# ── 1. Start SQL Server Demo Database Container (if Docker available) ────────
if [ -f "./setup-sqlserver-demo.sh" ]; then
    echo -e "${YELLOW}Checking SQL Server container...${NC}"
    # Run setup-sqlserver-demo.sh non-destructively
    ./setup-sqlserver-demo.sh || echo -e "${YELLOW}⚠️ Could not start SQL Server Docker automatically. You can start it manually with: sudo ./setup-sqlserver-demo.sh${NC}"
fi

# ── 2. Find dotnet command ───────────────────────────────────────────────────
DOTNET_CMD="dotnet"
if [ -f "/home/hirazone/.dotnet/dotnet" ]; then
    DOTNET_CMD="/home/hirazone/.dotnet/dotnet"
elif command -v dotnet >/dev/null 2>&1; then
    DOTNET_CMD="dotnet"
else
    echo -e "${RED}Error: .NET SDK is not installed or not found in PATH.${NC}"
    exit 1
fi

echo -e "Using .NET command: ${GREEN}${DOTNET_CMD}${NC}"

# ── 3. Release port 5057 & 7280 if they are already in use ───────────────────
echo -e "${BLUE}Checking for active processes on ports 5057 or 7280...${NC}"
if command -v fuser >/dev/null 2>&1; then
    fuser -k 5057/tcp 7280/tcp >/dev/null 2>&1 || true
elif command -v lsof >/dev/null 2>&1; then
    PIDS=$(lsof -t -i:5057 -i:7280 || true)
    if [ -n "$PIDS" ]; then
        echo -e "${YELLOW}Killing processes using ports 5057/7280: $PIDS${NC}"
        kill -9 $PIDS || true
    fi
fi

# ── 4. Run the web application ───────────────────────────────────────────────
echo -e "${GREEN}Starting SSIS Lineage Web Portal on http://localhost:5057...${NC}"
$DOTNET_CMD run --project src/SsisLineage.Web/SsisLineage.Web.csproj -f net10.0
