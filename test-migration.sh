#!/bin/bash
# Script to simulate and validate the exported SSIS Migration project
set -e

ZIP_FILE=$1

if [ -z "$ZIP_FILE" ]; then
    echo "Usage: ./test-migration.sh <path_to_exported_zip>"
    echo "Example: ./test-migration.sh ~/Downloads/Modern_Data_Engineering_Project.zip"
    exit 1
fi

PROJECT_DIR="/tmp/ssis_migration_test"

echo "=========================================="
echo "🚀 SSIS Migration Validation Simulation"
echo "=========================================="

echo "[1/4] 🧹 Cleaning up old test directories and containers..."
# Stop old containers if still running
if [ -f "$PROJECT_DIR/docker-compose.yml" ]; then
    (cd $PROJECT_DIR && docker compose down -v 2>/dev/null) || true
fi
# Force remove containers by name if they are stuck
docker rm -f webserver scheduler postgres ssis_migration_test-airflow-init-1 2>/dev/null || true

# Force remove folder using docker to avoid 'Permission denied' from root __pycache__
docker run --rm -v /tmp:/target alpine rm -rf /target/ssis_migration_test 2>/dev/null || true

mkdir -p $PROJECT_DIR

echo "[2/4] 📦 Extracting exported project..."
unzip -q "$ZIP_FILE" -d $PROJECT_DIR
cd $PROJECT_DIR

echo "[3/4] 🐳 Starting Modern Data Stack (Docker)..."
echo "This will download images (if not present) and start Airflow & Postgres."
make up

echo "⏳ Waiting for Airflow Webserver to be ready (approx 30 seconds)..."
sleep 30

echo "[4/4] 🧪 Running Data Validation & DAG Tests inside Container..."
# Run pytest (test_dag_validity.py) inside webserver
docker exec webserver bash -c "pip install pytest && pytest tests/dags/ -v"

echo "=========================================="
echo "✅ Validation Complete!"
echo "If the tests above PASSED, it means all generated Python/DAG code"
echo "from SSIS is syntactically valid and loaded successfully by Airflow!"
echo ""
echo "You can check the Airflow UI at http://localhost:8080"
echo "To tear down this test infrastructure, run: cd $PROJECT_DIR && make down"
echo "=========================================="
