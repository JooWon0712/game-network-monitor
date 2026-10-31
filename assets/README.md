# 앱 아이콘

이 폴더의 아이콘과 문서는 저장소의 [MIT 라이선스](../LICENSE)로 제공합니다. 사용·수정·재배포·상업적 이용이 가능하며, 재배포 시 원문의 저작권·라이선스 고지를 유지하세요. 아이콘 원본과 변환 파일에도 같은 조건을 적용합니다.

- `app-icon.png`: 내장 이미지 생성 도구(`image_gen`)로 만든 원본. 남색 바탕과 청록색 네트워크 신호를 사용하며 글자는 넣지 않았다.
- `app-icon.ico`: 원본의 투명도를 유지해 Pillow로 크기 변환·ICO 포장한 Windows 아이콘. 16·20·24·32·40·48·64·96·128·256px의 32비트 DIB 이미지를 포함한다.
- 빌드 시 같은 ICO를 Windows 실행 파일 아이콘과 `PcInternetMonitor.AppIcon.ico` 관리 리소스로 내장한다. 배포된 `PcInternetMonitor.exe` 옆에 이미지 파일이 없어도 창 제목·작업 표시줄에서 사용한다.

현재 제품은 PC 인터넷 모니터이며 저장소 이름은 `pc-internet-monitor`다. 아래는 아이콘 제작 당시의 원본 생성 프롬프트이므로 당시 제품명과 용도를 그대로 보존한다.

원본 생성 프롬프트(과거 기록):

```text
Use case: logo-brand
Asset type: Windows desktop application icon for Game Network Monitor, used at 16, 24, 32, 48 and 256 pixels.
Primary request: create one polished, distinctive app icon for a desktop tool that checks network response and connection quality while gaming.
Subject: a bold continuous cyan network signal waveform linking two simple endpoint nodes. Compact geometric symbol with a low peak then a strong central angular pulse, unified silhouette, not a medical ECG illustration. A tiny restrained white accent may emphasize the signal.
Composition: one centered icon only, front-facing, square canvas. A dark navy rounded-square tile fills most of the canvas with modest transparent margin; signal is centered and occupies about 70 percent of the tile width. Use chunky strokes and very few details so the mark is recognizable at 16 px.
Style: professional modern Windows utility branding, crisp flat vector-like shapes with a very subtle tonal navy tile gradient. Navy and vivid cyan match an existing dark UI, excellent contrast on both white title bars and dark taskbars.
Constraints: transparent background outside the rounded tile, opaque tile. No text, no letters, no numbers, no badges, no shield, no game controller, no Wi-Fi fan, no 3D perspective, no mockup, no multiple icons, no watermark. No bloom or blurry glow. Make the final artwork ready to convert to a multi-size ICO.
```
