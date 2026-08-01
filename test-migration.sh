#!/bin/bash
# Script untuk mensimulasikan dan memvalidasi hasil export Migrasi SSIS
set -e

ZIP_FILE=$1

if [ -z "$ZIP_FILE" ]; then
    echo "Usage: ./test-migration.sh <path_to_exported_zip>"
    echo "Contoh: ./test-migration.sh ~/Downloads/Modern_Data_Engineering_Project.zip"
    exit 1
fi

PROJECT_DIR="/tmp/ssis_migration_test"

echo "=========================================="
echo "🚀 SSIS Migration Validation Simulation"
echo "=========================================="

echo "[1/4] 🧹 Membersihkan direktori dan container test lama..."
# Menghentikan container lama jika masih berjalan
if [ -f "$PROJECT_DIR/docker-compose.yml" ]; then
    (cd $PROJECT_DIR && docker compose down -v 2>/dev/null) || true
fi
# Hapus paksa container berdasarkan nama jika masih nyangkut
docker rm -f webserver scheduler postgres ssis_migration_test-airflow-init-1 2>/dev/null || true

# Menghapus paksa folder menggunakan docker agar tidak kena 'Permission denied' dari __pycache__ root
docker run --rm -v /tmp:/target alpine rm -rf /target/ssis_migration_test 2>/dev/null || true

mkdir -p $PROJECT_DIR

echo "[2/4] 📦 Mengekstrak proyek hasil export..."
unzip -q "$ZIP_FILE" -d $PROJECT_DIR
cd $PROJECT_DIR

echo "[3/4] 🐳 Menyalakan Modern Data Stack (Docker)..."
echo "Ini akan men-download image (jika belum ada) dan menjalankan Airflow & Postgres."
make up

echo "⏳ Menunggu Airflow Webserver siap (sekitar 30 detik)..."
sleep 30

echo "[4/4] 🧪 Menjalankan Data Validation & DAG Tests di dalam Container..."
# Menjalankan pytest yang sudah kita edit (test_dag_validity.py) di dalam webserver
docker exec webserver bash -c "pip install pytest && pytest tests/dags/ -v"

echo "=========================================="
echo "✅ Validasi Selesai!"
echo "Jika test di atas berstatus PASSED, artinya seluruh kode Python/DAG hasil"
echo "generasi dari SSIS berhasil dibaca oleh mesin Airflow tanpa error syntax!"
echo ""
echo "Anda bisa mengecek Airflow UI di http://localhost:8080"
echo "Untuk mematikan infrastruktur test ini, jalankan: cd $PROJECT_DIR && make down"
echo "=========================================="
