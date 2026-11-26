# Oyun Planı (çalışma adı: belirlenmedi)

Arkadaşlarla oynanacak, 2–4 kişilik **co-op korku** oyunu. Takım dışarıdan kapalı bir yapıya girer, ganimet toplar ve kotayı doldurmaya çalışır. İçeride oyuncuların **sesini, adını seslenişini ve yürüyüşünü** taklit eden yaratıklar vardır. Hangi sesin gerçek olduğu anlaşılmamalıdır.

Hedef: önce arkadaşlarla oynanabilen bir sürüm, iyi olursa Steam.

---

## 1. Araçlar (hepsi ücretsiz)

| Konu | Araç | Not |
|---|---|---|
| Motor | **Unity 6.3 LTS** + **URP** | Unity Personal yıllık 200 bin $ gelire kadar ücretsiz |
| Dil | **C#** | |
| IDE | Visual Studio 2022 Community veya VS Code (C# Dev Kit) | Unity Hub kurulumunda VS seçilebilir |
| Multiplayer | **Netcode for GameObjects (NGO)** | Unity'nin resmî paketi. Host-client yapısı, sunucu masrafı yok |
| Steam | **Facepunch.Steamworks** + **Facepunch Transport** (NGO için) | Lobi, davet ve sesli sohbet. Test için AppID **480** (Spacewar), satarken kendi AppID'miz (Steam Direct 100$) |
| Yerel test | **Unity Transport** + **Multiplayer Play Mode** (Unity 6) | Steam olmadan tek PC'de birden fazla oyuncuyla test |
| Sesli sohbet | Steam Voice (`SteamUser.ReadVoice` / `DecompressVoice`) | Ücretsiz. Ham ses elimizde olduğu için taklit kolay |
| Konuşma tanıma (isimler) | **whisper.unity** (whisper.cpp'nin Unity sarmalayıcısı, MIT) | Çevrimdışı, Türkçe destekli. Aşama 4b'de |
| Modeller / sesler | Unity Asset Store ücretsizleri, Kenney, Quaternius, Mixamo, freesound.org | Lisanslar `CREDITS.md`'de tutulacak |
| Versiyon kontrol | Git + **Git LFS** (GitHub) | Asset serileştirme **Force Text** olacak |

## 2. Birlikte nasıl çalışacağız

Claude bulut ortamında çalışıyor, **Unity editörünü açamaz**. İş bölümü şöyle:

- **Claude:** C# scriptleri, oyun sistemleri, ağ kodu, ses taklidi, yapay zekâ ve iç mekân üretimi. Ayrıca editörde yapacağın her işi adım adım tarif eder.
- **Sen:** Unity'de sahneleri kurar, prefab'lara scriptleri bağlar, test edersin. Hata çıkarsa Console'daki mesajı Claude'a yapıştırırsın.
- Sahne ve prefab dosyaları **metin (YAML)** olarak kaydedildiği için Claude onları okuyabilir, gerektiğinde küçük düzeltmeler yapabilir.
- Akış: Claude bir dala push eder → sen `git pull` yapıp Unity'de test edersin → sonucu söylersin.

## 3. Oyun döngüsü

1. **Dış alan:** Takım aracından iner (ulaşım temaya göre belirlenecek). Dışarıda tehlike az, atmosfer var.
2. **Giriş kapısı:** Kapıdan girince oyuncu **kapalı, rastgele üretilmiş iç mekâna** ışınlanır. İç mekân aynı sahnede, haritanın çok altında ayrı bir bölgede durur.
3. **İç mekân:** Karanlık koridorlar, odalar, ganimet, tuzaklar ve taklitçiler.
4. **Dönüş:** Ganimetler araca taşınır. Gün bitmeden dönmeyen kalır.
5. **Kota:** Birkaç günde bir kota dolmalı. Dolmazsa oyun biter, dolarsa kazanılan parayla dükkândan alet alınır.

## 4. Taklitçi (Mimic) sistemi: oyunun kalbi

### 4a. Ses kaydı
- Her oyuncunun sesi Steam Voice ile zaten sıkıştırılmış paketler olarak ağdan gönderiliyor. Konuşanın **kendi bilgisayarı**, bir konuşma bitince (sessizlik > 0.4 sn) o paketleri bir **klip** olarak saklar (0.4–6 sn).
- Klipler host'a gönderilir, host da diğer oyunculara dağıtır. Böylece taklitçi herkeste aynı sesi çalar.
- Oyuncu başına havuz: son ~30 klip. Klipler bellekte durur, oyun kapanınca silinir.
- Ayarlarda **"sesimin taklit edilmesine izin ver"** seçeneği olur (gizlilik).

### 4b. Klip etiketleme (isimlere öncelik)
Klipler türüne göre etiketlenir:
- `ISIM_SESLENME`: içinde bir oyuncunun adı geçiyor ("Ahmet!", "Mehmet neredesin")
- `KISA_CEVAP`: kısa klip (< 1.2 sn): "evet", "geliyorum", "burada"
- `SORU` / `GENEL`: geri kalanlar

**İlk sürüm (basit):** sadece süreye göre etiketlenir. Kısa klipler cevap ya da seslenme olarak kullanılır.
**Gelişmiş sürüm:** Klip, konuşanın kendi bilgisayarında **whisper.unity** ile yazıya çevrilir. Lobide her oyuncu "sana nasıl seslenirler?" alanına takma adlarını yazar ("Ahmet, Ahmo, Memo"). Yazıya çevrilen metin bu adlarla bulanık (fuzzy) eşleştirilir ve klip hangi oyuncunun adını içeriyorsa ona göre işaretlenir.

### 4c. Taklitçi ne zaman konuşur
- **Sohbete karışma:** Taklitçinin yakınında gerçek bir oyuncu konuştuktan sonra, doğal bir 0.6–1.8 sn'lik aradan sonra taklit ettiği kişinin `KISA_CEVAP` klibiyle "cevap verir".
- **İsimle çağırma:** Grubundan ayrılmış bir oyuncuyu, başka bir oyuncunun sesiyle **o oyuncunun adını seslenerek** karanlık bir yere çeker.
- **Rastgele sohbet:** Ara sıra `GENEL` klipleri çalar.
- Kliplerin aynısı arka arkaya tekrarlanmaz (tekrar oyunu ele verir).

### 4d. Ayırt edilemezlik kuralları
- Gerçek oyuncu sesi ve taklitçi sesi **aynı `VoicePlayer` bileşeninden** çalınır: aynı AudioSource ayarları, aynı 3D uzaklık eğrisi, aynı duvar arkası boğulma (low-pass), aynı AudioMixer grubu. Kod olarak tek fark sesin kaynağıdır (canlı ağ verisi ya da kayıtlı klip).
- Telsiz (walkie-talkie) eklenirse taklitçi telsizden de konuşabilir.
- Arayüzde "bu ses kimden geliyor" gibi bir ipucu **olmayacak**. İsim etiketleri de sadece yakında ve ışıkta görünecek, ya da hiç olmayacak.

### 4e. Görünüş ve yürüyüş taklidi
- Taklitçi, oyuncu karakteriyle **aynı model, kostüm/renk, elde tutulan eşya ve Animator Controller**'ı kullanır. Animasyonlar da aynı parametrelerle (hız, eğilme, bakış) sürülür.
- Her oyuncunun hareketinden bir **"hareket parmak izi"** çıkarılır: ortalama hız, koşma/yürüme oranı, eğilme sıklığı, durup etrafa bakma süreleri, zıplama sıklığı, fener açma-kapama alışkanlığı, kafa (bakış) hareket hızı.
- Taklitçinin yapay zekâsı (NavMeshAgent) hareket ederken bu profilden örnekler. Böylece hızlı koşan birini taklit eden taklitçi de hızlı koşar, sürekli eğilen birini taklit eden de eğilir.
- Gelişmiş sürüm: oyuncunun gerçek hareket izinden kısa parçalar (pozisyon/rotasyon) kaydedilir ve uygun yerlerde tekrar oynatılır.
- **Küçük "kusurlar"** bırakılır ki dikkatli oyuncu yakalayabilsin (ör. çok yakından bakınca göz kırpmama, merdivende takılma). Bunlar oyunun dengesi için, test ederek ayarlanacak.

## 5. İç mekân üretimi
- Oda tabanlı prosedürel üretim, kendi C# kodumuzla (ücretli eklenti yok).
- Her oda bir prefab. Kapı noktaları (`Doorway` transform'ları) var, odalar kapılardan birbirine bağlanır. Çakışma kontrolü bounding box ile yapılır.
- Aynı **seed** ile host ve client'larda aynı bina üretilir, böylece ağ üzerinden bütün haritayı göndermeye gerek kalmaz.
- Üretimden sonra NavMesh çalışma anında yeniden hesaplanır (AI Navigation paketi, `NavMeshSurface`).

## 6. Proje klasör yapısı

```
Assets/
  _Project/
    Scripts/
      Player/        hareket, kamera, envanter, fener
      Network/       bağlantı, lobi, Steam
      Voice/         sesli sohbet, klip kaydı, VoicePlayer
      Mimic/         taklitçi yapay zekâsı, hareket profili
      Dungeon/       iç mekân üretimi
      Items/         ganimet, aletler
      Game/          gün/kota/dükkân döngüsü
      UI/
    Prefabs/
    Scenes/
    Materials/
    Audio/
    Models/
  ThirdParty/        dışarıdan gelen asset'ler
```

## 7. Yol haritası

| Aşama | İçerik | Bitiş kriteri |
|---|---|---|
| **0. Kurulum** ✅ | Unity 6.3 LTS + URP projesi, Force Text, git/LFS, NGO paketi, ilk push | Proje açılıyor, repoda |
| **1. Tek oyunculu temel** ✅ | Birinci şahıs hareket (CharacterController), eğilme, koşma, el feneri, envanter (4 slot), eşya alma/bırakma, ganimet değeri | Test sahnesinde eşya toplayıp araca bırakabiliyorum |
| **2. Multiplayer** ✅ | NGO host/client, oyuncu ve eşya senkronizasyonu, yerel test (Multiplayer Play Mode) → Steam lobi/davet (AppID 480) | 2 bilgisayarda birlikte eşya toplayabiliyoruz |
| **3. Sesli sohbet** | Steam Voice, `VoicePlayer`, 3D ses, duvar arkası boğulma, ölünce izleyici modu | Uzaklaşınca ses azalıyor |
| **4. Taklitçi** | 4a klip kaydı → 4c davranış → 4e görünüş/yürüyüş → 4b whisper ile isimler | Arkadaşlar taklitçiye kandı 🎉 |
| **5. İç mekân üretimi** | Giriş kapısı/ışınlanma, prosedürel odalar, NavMesh, ganimet dağıtımı | Her oyunda farklı bina |
| **6. Oyun döngüsü** | Gün/saat, kota, dükkân, oyun sonu | Baştan sona bir tur oynanıyor |
| **7. Cila** | Ses tasarımı, ışık, post-process (grain, vignette), menüler, ayarlar | Arkadaşlara verilebilir sürüm |
| **8. Steam** | Kendi AppID, mağaza sayfası, Early Access | Yayında |

## 8. Açık kararlar
- [x] Motor: **Unity 6.3 LTS**
- [ ] **Tema / mekân** (aşağıdaki fikirlerden biri ya da başka bir şey)
- [ ] Oyunun adı
- [ ] Ulaşım aracı (temaya bağlı)

### Tema fikirleri
1. **Maden kasabası:** Karlı, terk edilmiş bir maden kasabası. Takım eski bir yük asansörüyle yer altındaki tünellere ve işçi koğuşlarına iner. Taklitçiler kaybolan madencilerden "öğrenmiş".
2. **Sisli adalar:** Takım küçük bir tekneyle sisli adalardaki deniz fenerlerine ve su basmış araştırma binalarına gider. Sis yüzünden dışarıda bile kimin kim olduğu belirsizdir.
3. **Karantina bölgesi:** Hurdacı bir ekip karavanla karantinaya alınmış bir şehre girer. Hastaneler, radyo istasyonları ve alışveriş merkezleri vardır. Radyo yayınlarında da sesler taklit edilir.
