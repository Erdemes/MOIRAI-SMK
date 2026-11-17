# MOIRAI — Demo Paketi

Üst yönetim sunumu için hazırlanmış, **motorda doğrulanmış** demo senaryoları ve sunum.

## İçerik

| Dosya | Ne işe yarar |
|---|---|
| `sunum.html` | Kendi içinde tam, offline çalışan slayt destesi. Tarayıcıda aç; ← → / Space, **F** tam ekran. |
| `DEMO-KILAVUZU.md` | Adım adım sunum/demo betiği: hangi sayfaya tıkla, hangi rakam çıkar. |
| `scenarios/01-spring-summer-2026.json` | Senaryo 1 — bir kampanya kayar; neden = uçak; öneri = +1 uçak. |
| `scenarios/02-winter-2026-27.json` | Senaryo 2 — nominal hepsi yeşil ama Monte Carlo gizli riski açar; öneri = kapsam (test puanı/sorti). |

## Senaryoyu uygulamada açmak

1. `dotnet run --project Moirai.App`
2. **File → Open Scenario…** → `demo/scenarios/…json`
3. **Plan** sayfasında çizelge/Gantt; **Analysis** sayfasında Monte Carlo + Resource recommendation.

Açılan senaryo çalışma dosyasına (`moirai.json`) kopyalanır; bu klasördeki orijinaller değişmez.

## Doğrulanmış sonuçlar (N=500, sabit seed)

**Senaryo 1 (İlkbahar/Yaz):** PERF/AVI/WPN tutuyor; Flutter geç (2026-07-03 > 06-30),
dominant bottleneck = aircraft (31 gün), P(meet)=%0 → **+1 uçak → %100**.

**Senaryo 2 (Kış):** nominal hepsi tutuyor; Monte Carlo: Cold-Weather %68, Icing %76,
Navigation %88 → öneri (hedef %80): 3× **test puanı/sorti** artışı ile hepsi ≥%80
(uçak/ekip eklemek hava-bağımlı kısıtı çözmez).

> Rakamlar `MOIRAI.Tests` içindeki geçici bir harness ile motora karşı doğrulanmıştır.
> Harness commit edilmedi; sonuçlar `DEMO-KILAVUZU.md` ve `sunum.html` içine işlendi.
