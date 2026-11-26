# Oyun Planı (çalışma adı: belirlenmedi)

Arkadaşlarla oynanacak, 2–4 kişilik **co-op korku** oyunu. Takım dışarıdan kapalı bir yapıya girer, ganimet toplar ve kotayı doldurmaya çalışır. İçeride oyuncuların **sesini, adını seslenişini ve yürüyüşünü** taklit eden yaratıklar vardır. Hangi sesin gerçek olduğu anlaşılmamalıdır.

Hedef: önce arkadaşlarla oynanabilen bir sürüm, iyi olursa Steam.

---

## 1. Araçlar (hepsi ücretsiz)

| Konu | Araç | Not |
|---|---|---|
| Motor | **Unreal Engine 5** (en güncel sürüm) | 1M$ gelire kadar ücretsiz, sonrasında %5 |
| Kod | **C++** (+ gerektiğinde Blueprint) | Kodu Claude yazar, sen derleyip editörde test edersin |
| IDE | Visual Studio 2022 Community veya Rider (öğrenci/ücretsiz) | VS kurulumunda "Game development with C++" seçili olmalı |
| Multiplayer | Unreal'ın kendi replikasyon sistemi (Listen Server) | Bir oyuncu host olur, sunucu masrafı yok |
| Lobi / davet | **Online Subsystem Steam** | Test için AppID **480** (Spacewar), satarken kendi AppID'miz (Steam Direct 100$) |
| Sesli sohbet | Unreal VOIP (Steam üzerinden) | Ücretsiz, konumsal ses destekli |
| Konuşma tanıma (isimler) | **whisper.cpp** (MIT lisans, çevrimdışı, Türkçe destekli) | Aşama 4b'de |
| Modeller / sesler | Fab ücretsiz içerikler, Quixel Megascans, Mixamo animasyonları, freesound.org | Lisansları kaydedilecek (`CREDITS.md`) |
| Versiyon kontrol | Git + **Git LFS** (GitHub) | GitHub LFS ücretsiz kotası 1 GB. Dolarsa Azure DevOps'a geçeriz |

## 2. Birlikte nasıl çalışacağız

Claude bulut ortamında çalışıyor, **Unreal editörünü açamaz**. İş bölümü şöyle:

- **Claude:** C++ sınıfları, oyun sistemleri, ağ kodu, ses taklidi sistemi, config dosyaları. Ayrıca editörde yapacağın her işi adım adım tarif eder.
- **Sen:** Projeyi derlersin, editörde harita/Blueprint/asset işlerini yapar, oyunu test edersin. Hataları ve log'ları (`Saved/Logs/*.log`) Claude'a yapıştırırsın.
- Kod mantığı Blueprint yerine **C++'ta** olacak, çünkü Blueprint dosyaları ikili (binary) dosya ve Claude onları okuyamaz. Blueprint'ler sadece C++ sınıflarının görsel ayarı (mesh, ses, sayı değerleri) için kullanılacak.

## 3. Oyun döngüsü

1. **Dış alan:** Takım aracından iner (ulaşım temaya göre belirlenecek). Dışarıda tehlike az, atmosfer var.
2. **Giriş kapısı:** Kapıdan girince oyuncu **kapalı, rastgele üretilmiş iç mekâna** ışınlanır. İç mekân aynı haritada, yer altında ayrı bir bölgede durur.
3. **İç mekân:** Karanlık koridorlar, odalar, ganimet, tuzaklar ve taklitçiler.
4. **Dönüş:** Ganimetler araca taşınır. Gün bitmeden dönmeyen kalır.
5. **Kota:** Birkaç günde bir kota dolmalı. Dolmazsa oyun biter, dolarsa kazanılan parayla dükkândan alet alınır.

## 4. Taklitçi (Mimic) sistemi: oyunun kalbi

### 4a. Ses kaydı
- Her oyuncu **kendi mikrofonunu kendi bilgisayarında** kaydeder. Bir eşik üstünde konuşma algılanınca (VAD) 0.4–6 sn'lik klip kesilir.
- Klip Opus ile sıkıştırılıp parça parça host'a gönderilir. Host da diğer oyunculara dağıtır, böylece taklitçi herkeste aynı sesi çalar.
- Oyuncu başına havuz: son ~30 klip. Klipler bellekte durur, oyun kapanınca silinir.
- Ayarlarda **"sesimin taklit edilmesine izin ver"** seçeneği olur (gizlilik).

### 4b. Klip etiketleme (isimlere öncelik)
Klipler türüne göre etiketlenir:
- `ISIM_SESLENME`: içinde bir oyuncunun adı geçiyor ("Ahmet!", "Mehmet neredesin")
- `KISA_CEVAP`: kısa klip (< 1.2 sn): "evet", "geliyorum", "burada"
- `SORU` / `GENEL`: geri kalanlar

**İlk sürüm (basit):** sadece süreye göre etiketlenir. Kısa klipler cevap ya da seslenme olarak kullanılır.
**Gelişmiş sürüm:** Klip, konuşanın kendi bilgisayarında **whisper.cpp** ile yazıya çevrilir. Lobide her oyuncu "sana nasıl seslenirler?" alanına takma adlarını yazar ("Ahmet, Ahmo, Memo"). Yazıya çevrilen metin bu adlarla bulanık (fuzzy) eşleştirilir ve klip hangi oyuncunun adını içeriyorsa ona göre işaretlenir.

### 4c. Taklitçi ne zaman konuşur
- **Sohbete karışma:** Taklitçinin yakınında gerçek bir oyuncu konuştuktan sonra, doğal bir 0.6–1.8 sn'lik aradan sonra taklit ettiği kişinin `KISA_CEVAP` klibiyle "cevap verir".
- **İsimle çağırma:** Grubundan ayrılmış bir oyuncuyu, başka bir oyuncunun sesiyle **o oyuncunun adını seslenerek** karanlık bir yere çeker.
- **Rastgele sohbet:** Ara sıra `GENEL` klipleri çalar.
- Kliplerin aynısı arka arkaya tekrarlanmaz (tekrar oyunu ele verir).

### 4d. Ayırt edilemezlik kuralları
- Taklitçinin sesi **gerçek sesli sohbetle birebir aynı yoldan** çalınır: aynı attenuation (uzaklık sönümü), aynı oklüzyon (duvar arkası boğulma), aynı submix/efekt, aynı "konuşuyor" göstergesi (varsa).
- Telsiz (walkie-talkie) eklenirse taklitçi telsizden de konuşabilir.
- Arayüzde "bu ses kimden geliyor" gibi bir ipucu **olmayacak**. İsim etiketleri de sadece yakında ve ışıkta görünecek, ya da hiç olmayacak.

### 4e. Görünüş ve yürüyüş taklidi
- Taklitçi, oyuncu karakteriyle **aynı C++ sınıfından türetilir**: aynı model, aynı kostüm/renk, aynı elde tutulan eşya, aynı animasyon sistemi.
- Her oyuncunun hareketinden bir **"hareket parmak izi"** çıkarılır: ortalama hız, koşma/yürüme oranı, eğilme sıklığı, durup etrafa bakma süreleri, zıplama sıklığı, fener açma-kapama alışkanlığı, kafa (bakış) hareket hızı.
- Taklitçinin yapay zekâsı hareket ederken bu profilden örnekler. Böylece hızlı koşan birini taklit eden taklitçi de hızlı koşar, sürekli eğilen birini taklit eden de eğilir.
- Gelişmiş sürüm: oyuncunun gerçek hareket izinden kısa parçalar (pozisyon/rotasyon) kaydedilir ve uygun yerlerde tekrar oynatılır.
- **Küçük "kusurlar"** bırakılır ki dikkatli oyuncu yakalayabilsin (ör. çok yakından bakınca göz kırpmama, merdivende takılma). Bunlar oyunun dengesi için, test ederek ayarlanacak.

## 5. İç mekân üretimi
- Oda tabanlı prosedürel üretim, kendi C++ kodumuzla (ücretli eklenti yok).
- Her oda bir "parça" (Level Instance ya da Actor). Kapı noktaları (socket) var, odalar kapılardan birbirine bağlanır. Çakışma kontrolü bounding box ile yapılır.
- Aynı **seed** ile host ve client'larda aynı bina üretilir, böylece ağ üzerinden bütün haritayı göndermeye gerek kalmaz.

## 6. Yol haritası

| Aşama | İçerik | Bitiş kriteri |
|---|---|---|
| **0. Kurulum** | UE5 + VS kurulumu, C++ First Person projesi, git/LFS, ilk push | Proje derleniyor, repoda |
| **1. Tek oyunculu temel** | Hareket, eğilme, koşma, el feneri, envanter (4 slot), eşya alma/bırakma, ganimet değeri | Test haritasında eşya toplayıp araca bırakabiliyorum |
| **2. Multiplayer** | Listen server, Steam lobi/davet (AppID 480), tüm sistemlerin replikasyonu | 2 bilgisayarda birlikte eşya toplayabiliyoruz |
| **3. Sesli sohbet** | Konumsal VOIP, duvar arkası boğulma, ölünce izleyici modu | Uzaklaşınca ses azalıyor |
| **4. Taklitçi** | 4a ses kaydı → 4c davranış → 4e görünüş/yürüyüş → 4b whisper ile isimler | Arkadaşlar taklitçiye kandı 🎉 |
| **5. İç mekân üretimi** | Giriş kapısı/ışınlanma, prosedürel odalar, ganimet dağıtımı | Her oyunda farklı bina |
| **6. Oyun döngüsü** | Gün/saat, kota, dükkân, oyun sonu | Baştan sona bir tur oynanıyor |
| **7. Cila** | Ses tasarımı, ışık, post-process, menüler, ayarlar | Arkadaşlara verilebilir sürüm |
| **8. Steam** | Kendi AppID, mağaza sayfası, Early Access | Yayında |

## 7. Açık kararlar
- [ ] **Tema / mekân** (aşağıdaki fikirlerden biri ya da başka bir şey)
- [ ] Oyunun adı
- [ ] Ulaşım aracı (temaya bağlı)

### Tema fikirleri
1. **Maden kasabası:** Karlı, terk edilmiş bir maden kasabası. Takım eski bir yük asansörüyle yer altındaki tünellere ve işçi koğuşlarına iner. Taklitçiler kaybolan madencilerden "öğrenmiş".
2. **Sisli adalar:** Takım küçük bir tekneyle sisli adalardaki deniz fenerlerine ve su basmış araştırma binalarına gider. Sis yüzünden dışarıda bile kimin kim olduğu belirsizdir.
3. **Karantina bölgesi:** Hurdacı bir ekip karavanla karantinaya alınmış bir şehre girer. Hastaneler, radyo istasyonları ve alışveriş merkezleri vardır. Radyo yayınlarında da sesler taklit edilir.
