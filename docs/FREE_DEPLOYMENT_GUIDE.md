# 🚀 Panduan Deployment Gratis (Zero-Cost Hosting)

SSIS Lineage Hub dibangun menggunakan **Blazor Server (.NET 10)**. Karena aplikasi ini membutuhkan *runtime* server (bukan sekadar file statis HTML/JS), cara termudah dan **100% gratis** untuk men-deploy-nya adalah dengan menggunakan layanan *Cloud Container* yang memiliki **Free Tier**. 

Kami telah menyediakan `Dockerfile` di root proyek ini. Dengan Dockerfile ini, aplikasi bisa di-deploy ke berbagai penyedia cloud gratis. 

Berikut adalah 2 rekomendasi platform terbaik untuk hosting gratis:

---

## Opsi 1: Render.com (Sangat Direkomendasikan & Mudah)

Render memiliki tier "Free Web Service" yang memungkinkan Anda untuk menghubungkan repositori GitHub secara langsung. Render akan membaca `Dockerfile`, melakukan build, dan me-run aplikasi secara otomatis.

**Kelebihan:** Tanpa kartu kredit (biasanya). Sangat mudah untuk pemula.
**Kekurangan:** Aplikasi akan "tidur" (*spin down*) jika tidak ada pengunjung selama 15 menit. Saat pengunjung pertama datang, butuh waktu sekitar 30-50 detik untuk *cold boot* (menyala kembali).

### Langkah-langkah:
1. Buat akun di [Render.com](https://render.com).
2. Klik tombol **New +** dan pilih **Web Service**.
3. Hubungkan akun GitHub Anda dan pilih repositori `ssis-lineage-hub`.
4. Pada halaman konfigurasi:
   - **Name:** ssis-lineage-hub
   - **Language:** Docker (Render akan otomatis mendeteksinya)
   - **Branch:** main
   - **Instance Type:** Free
5. Klik **Create Web Service**.
6. Render akan mulai mem-build kontainer. Tunggu beberapa menit, dan aplikasi Anda akan live di URL yang diberikan (misal: `ssis-lineage-hub.onrender.com`).

---

## Opsi 2: Koyeb (Performa Lebih Cepat & Tidak "Tidur")

Koyeb menawarkan *Free Tier* (1 instance Eco) yang terus menyala 24/7 tanpa mengalami *cold boot* seperti Render.

**Kelebihan:** Cepat, menyala 24/7, performa bagus.
**Kekurangan:** Biasanya mengharuskan Anda memasukkan kartu kredit untuk mencegah spam/abuse (meskipun tidak akan ditagih selama Anda menggunakan tier Eco gratis).

### Langkah-langkah:
1. Daftar di [Koyeb.com](https://app.koyeb.com/).
2. Klik **Create Service**.
3. Pilih metode deployment **GitHub** dan pilih repositori `ssis-lineage-hub`.
4. Koyeb akan mendeteksi `Dockerfile` secara otomatis.
5. Pada bagian **Instance**, pastikan Anda memilih **Eco / Free** (512MB RAM, 0.1 vCPU).
6. Di bagian **Ports**, pastikan diatur ke port `8080` (karena di dalam `Dockerfile` kita menggunakan port 8080).
7. Klik **Deploy**. Aplikasi akan online dalam waktu kurang dari 5 menit.

---

## 💡 Catatan Teknis (Environment Variables)

Aplikasi ini sudah dioptimalkan agar berjalan dalam kontainer. Di dalam `Dockerfile`, kami telah menambahkan:
```dockerfile
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
```
Hal ini memastikan Kestrel (web server bawaan .NET) berjalan pada port standar 8080, yang sangat kompatibel dengan aturan *load balancer* di platform cloud seperti Render, Koyeb, atau Fly.io.

## Bagaimana dengan Data / File SSIS?
Karena SSIS Lineage Hub mengandalkan *local parsing* (membaca file `.dtsx` dari sistem), saat aplikasi berjalan di cloud (Render/Koyeb), Anda perlu **meng-upload** file `.dtsx` atau `.dtproj` langsung dari UI web, alih-alih meletakkannya di folder cloud server. Pastikan fitur unggah (*upload to memory*) digunakan dengan baik.
