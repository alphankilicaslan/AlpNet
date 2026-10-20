import sys
import os
import speech_recognition as sr
from googletrans import Translator
from gtts import gTTS
from io import BytesIO
import pygame
import time
import tempfile
import soundfile as sf
import warnings

warnings.filterwarnings("ignore")

def play_audio(file_path, output_device_index=None):
    try:
        if output_device_index is not None and output_device_index >= 0:
            os.environ['SDL_AUDIODRIVER'] = 'dsound'
        pygame.mixer.init()
        pygame.mixer.music.load(file_path)
        pygame.mixer.music.play()
        while pygame.mixer.music.get_busy():
            pygame.time.Clock().tick(10)
        pygame.mixer.quit()
    except Exception as e:
        print(f"[SES HATASI] {e}")

def main():
    if len(sys.argv) < 5:
        print("Usage: translator.py <mic_index> <out_index> <src_lang> <target_lang>")
        sys.exit(1)

    mic_index = int(sys.argv[1])
    out_index = int(sys.argv[2])
    src_lang = sys.argv[3]
    target_lang = sys.argv[4]

    recognizer = sr.Recognizer()
    translator = Translator()

    if mic_index < 0:
        mic_index = None

    print(f"[SİSTEM] Mikrofon İndeksi: {mic_index}, Çıkış İndeksi: {out_index}")
    print(f"[SİSTEM] {src_lang.upper()} -> {target_lang.upper()} çevirisi başlatıldı...")

    try:
        with sr.Microphone(device_index=mic_index) as source:
            print("[DİNLEME] Ortam gürültüsü ayarlanıyor...")
            recognizer.adjust_for_ambient_noise(source, duration=1)
            print("[DİNLEME] Konuşmaya başlayabilirsiniz!")
            
            while True:
                try:
                    audio = recognizer.listen(source, timeout=2, phrase_time_limit=5)
                    print("[İŞLENİYOR] Ses algılandı, çevriliyor...")
                    
                    text = recognizer.recognize_google(audio, language=src_lang)
                    print(f"[SEN]: {text}")
                    
                    translated = translator.translate(text, src=src_lang, dest=target_lang)
                    print(f"[ÇEVİRİ]: {translated.text}")
                    
                    tts = gTTS(text=translated.text, lang=target_lang, slow=False)
                    temp_mp3 = tempfile.NamedTemporaryFile(delete=False, suffix='.mp3')
                    tts.save(temp_mp3.name)
                    temp_mp3.close()
                    
                    play_audio(temp_mp3.name, out_index)
                    
                    os.unlink(temp_mp3.name)
                    print("[DİNLEME] Dinleniyor...")

                except sr.WaitTimeoutError:
                    pass
                except sr.UnknownValueError:
                    print("[HATA] Sesiniz anlaşılamadı.")
                except Exception as e:
                    print(f"[HATA] Beklenmeyen hata: {e}")

    except Exception as e:
        print(f"[KRİTİK HATA] Mikrofon başlatılamadı: {e}")

if __name__ == "__main__":
    main()
