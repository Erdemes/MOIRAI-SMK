# MOIRAI — Üst Yönetim Demo Kılavuzu

Bu kılavuz, sunum sırasında ekranı adım adım nasıl yöneteceğini ve hangi rakamların
çıkacağını söyler. Tüm sayılar uygulamanın varsayılan ayarlarıyla (**Monte Carlo N=500,
sabit seed**) motorda **doğrulanmıştır** — canlı demo bu rakamları birebir gösterir.

> Sunum dosyası: **`demo/sunum.html`** — herhangi bir tarayıcıda aç (çift tıkla).
> Oklar / Space ile gezin, **F** tam ekran. İnternet gerekmez.

---

## 0 · Hazırlık (sunumdan önce)

1. Uygulamayı bir kez çalıştır, çalıştığını gör:
   ```powershell
   dotnet run --project Moirai.App
   ```
2. Sunum bilgisayarında **`demo/sunum.html`**'i tarayıcıda aç, tam ekran (F) dene.
3. İki senaryo dosyasının yerini bil:
   - `demo/scenarios/01-spring-summer-2026.json`
   - `demo/scenarios/02-winter-2026-27.json`
4. Senaryoyu uygulamaya yüklemek: **File → Open Scenario…** → dosyayı seç.
   (Yüklenen senaryo çalışma dosyası `moirai.json` olur; demo dosyaları bozulmaz çünkü
   orijinaller `demo/scenarios/` altında durur.)

> İpucu: Demoya başlamadan önce uygulamada **01** senaryosunu açık tut; slayttan canlıya
> geçiş akıcı olsun.

---

## 1 · Açılış (Slayt 1–4, ~3 dk)

Konuş, tıklama yok. Mesaj:
- MOIRAI, kampanya + kaynak + kuralları **gün-gün çizelgeye** döker.
- Sadece "ne zaman biter" değil; **neden kayar, ne kadar olası, nasıl düzeltilir.**
- Motor UI'dan bağımsız, FTOM kuralları gömülü, tüm limitler **veridir** (sabit kod yok).

---

## 2 · Senaryo 1 — İlkbahar/Yaz (Slayt 5–9, ~6 dk)

**Senaryoyu aç:** File → Open Scenario… → `01-spring-summer-2026.json`

### 2a. Çizelge (Plan sayfası)
- **Plan** sayfasına geç, planı çalıştır.
- **Gantt**'ı göster: uçak paneli (bakım bandı TC-PT03'te görünür), ekip paneli (sorti +
  kesik prova kutuları), hafta sonu/tatil gölgeleri, deadline çizgileri.
- Sonuç tablosu (Slayt 6 ile birebir):

  | Kampanya | Bitiş | Hedef | Durum |
  |---|---|---|---|
  | Performance Envelope | 2026-04-21 | 2026-05-29 | **TUTUYOR** |
  | Avionics & Systems | 2026-04-21 | 2026-07-31 | **TUTUYOR** |
  | Stores Separation | 2026-06-15 | 2026-08-31 | **TUTUYOR** |
  | Flutter / Aeroelastic | 2026-07-03 | 2026-06-30 | **GEÇ (3 gün)** |

- Soru bırak: *"Flutter neden kayıyor?"*

### 2b. Neden (Bottleneck)
- Bottleneck/diagnostic'i göster: **dominant bottleneck = aircraft (31 gün)**.
- Anlat: Flutter yalnız TC-PT03 ile uçar ve her sorti **tüm günü** bağlar; o uçak
  pencerenin neredeyse tamamında bakımda (05-01…06-22).
- Vurgu: araç **pilot/mühendis önermiyor** — kısıt onlar değil.

### 2c. Hava riski (Analysis sayfası)
- **Analysis** sayfasına geç, planı seç, **Run Monte Carlo** (N=500 varsayılan).
- Tablo (Slayt 8):

  | Kampanya | P(tutturma) | P50 | P90 |
  |---|---|---|---|
  | Performance Envelope | %100 | 2026-04-21 | 2026-04-30 |
  | Avionics & Systems | %100 | 2026-04-21 | 2026-04-30 |
  | Stores Separation | %100 | 2026-06-15 | 2026-06-18 |
  | Flutter / Aeroelastic | **%0** | 2026-07-02 | 2026-07-07 |

### 2d. Öneri (Resource recommendation)
- Aynı sayfada **Resource recommendation** kartı. Tüm lever'lar açık kalsın.
- **Recommend fixes**'e bas. Çıktı:
  - **+1 aircraft → Flutter / Aeroelastic %0 → %100**
  - Özet: *"All campaigns reach ≥ 85% confidence after 1 addition."*
- Mesaj: araç yalnız sorunu değil, **en ucuz çözümü** de gösterir.

---

## 3 · Senaryo 2 — Kış (Slayt 10–13, ~6 dk)

**Senaryoyu aç:** File → Open Scenario… → `02-winter-2026-27.json`

### 3a. Nominal "tuzak"
- Planı çalıştır. Tablo (Slayt 11): **hepsi TUTUYOR** görünür.
  - Cold-Weather 2026-12-03 / hedef 12-11
  - Icing 2026-12-23 / hedef 12-31
  - Navigation 2027-02-01 / hedef 02-20
- Anlat: *"Tek takvime bakarsanız program güvende. Ama bu yanıltıcı."*

### 3b. Monte Carlo gerçeği
- **Analysis → Run Monte Carlo** (N=500).

  | Kampanya | P(tutturma) | P90 |
  |---|---|---|
  | Cold-Weather Systems | **%68** | 2027-01-04 |
  | Icing Trials | **%76** | 2027-01-18 |
  | Navigation & Nav-Aids | **%88** | 2027-02-17 |

- Mesaj: "yeşil" plan gerçekte **her 3 kıştan 1'inde** Cold-Weather'ı kaçırıyor; P90'da
  iş Ocak'a sarkıyor. Kış havası **seri** halinde kötü gider.

### 3c. Akıllı öneri — "parayla hava satın alınmaz"
- **Resource recommendation** kartında:
  - **Target confidence (%)** = `80`
  - **Max steps** = `5`
  - Tüm lever'lar açık.
- **Recommend fixes**. Çıktı (sırasıyla, hepsi **test puanı/sorti** artışı):
  - Icing: 3 → 4 (%76 → %83)
  - Cold-Weather: 5 → 6 (%73 → %77)
  - Cold-Weather: 6 → 7 (%77 → %81)
  - Özet: *"All campaigns reach ≥ 80% confidence after 3 additions."*
- Mesaj: Hava bağlayıcı olunca **+uçak/+pilot/+mühendis işe yaramaz** (uçulabilir gün
  eklemez). Araç doğru leveri seçiyor: **kapsam** (test puanı/sorti). Diğer çıkışlar:
  pencereyi daha iyi mevsime kaydır ya da riski bilerek kabul et.

> İstersen kıyas için lever'lardan "Raise test points / sortie"yi kapatıp tekrar dene:
> araç hedefe ulaşamadığını **dürüstçe** söyler (hardware bu riski çözemez).

---

## 4 · Yöntem + Değer + Kapanış (Slayt 14–17, ~4 dk)

- **Neden Monte Carlo + Markov:** bağımsız günler riski hafife alır; 2-durumlu Markov
  zinciri uzun dönem oranı korur ama spellleri uzatır → gerçekçi kuyruk riski. Tüm
  adaylar aynı hava setinde (CRN) test edilir.
- **Değer:** erken uyarı, rakamla savunulabilir kaynak talepleri, doğru kaynağa yatırım,
  risk şeffaflığı, FTOM uyumu, saniyeler içinde what-if.
- **Durum/Yol haritası:** çekirdek + 4 yetenek hazır; Faz-2'de optimizasyon katmanı,
  saat-tabanlı kurallar, gece ops.

---

## Beklenmedik durum / yedek plan

- Canlı demo takılırsa: bu kılavuzdaki tablolar ve `sunum.html` zaten **doğrulanmış**
  rakamları içerir; slayttan devam et.
- Rakamlar birebir aynı çıkar çünkü seed sabit (N büyüdükçe yalnız kararlılık artar,
  sayı oynamaz). N'i düşürmek demoyu hızlandırır ama küçük sapma yapabilir.
- Senaryo dosyaları bozulmaz: uygulama açtığın dosyayı `moirai.json`'a kopyalar;
  `demo/scenarios/` altındaki orijinaller değişmez.
