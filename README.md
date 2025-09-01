# PCD OPEN (with UNITY)

## Description

Unity엔진으로 Point Cloud Data를 화면에 표시합니다.

이 Viewer는 .pcd 확장자 데이터를 확인하기 위해 제작되었습니다.

## Patch Note (ver 0.0.5)
	- EDL 적용이 불분명하던 부분을 재정립하여 작동하도록 수정했습니다.
	- 모든 Point가 렌더링되던 방식에서 화면에 보이는 부분만 렌더링 되도록 변경되었습니다.
    - 깊이에 따라 색상이 변경됩니다.
    - UI에 그래픽 옵션 추가 및 변경되었습니다.
	
## Build File
[PcdOpen_ver0.0.5 (Window)](https://drive.google.com/file/d/1fDlPgfsnFEjV5CzTSOUvaYDn0Cu1Yi9P/view?usp=sharing)

### Notice
- 용량이 높아질수록 불러오기 시간이 길어집니다.
- Root Sample 수치를 높이면 더 정확한 Point Cloud를 얻을 수 있지만 그래픽 로드율이 높아집니다.
- Gaussian 옵션을 조작하면 Point 모양이 변경됩니다.
- UI에 있는 옵션의 값을 조작해도 변경되지 않는 값들이 있습니다.

## Dev Note
<details>
<summary>#1</summary>

20250811 ~ 20250819
- 2GB 미만 .pcd 확장자 파일을 Unity에서 Load
- Load한 Point Cloud Data 조작 (마우스)
- Build 대응 간단한 UI 제작
- 메모리, 성능 확인용 Assets 'Graphy' 포함 (Key binding : F12)

</details>

<details>
<summary>#2</summary>

20250820 ~ 20250822
- 2GB 이상 .pcd 확장자 파일 불러오기 가능 (PC 사양에 따라 차이가 있음)
- Gpu 일괄 렌더링 -> Octree 알고리즘 렌더링 로직 변경 (참고 : Potree)
    - GPU에 한 번에 전부 올리는 것이 아닌 'chunk' 단위로 나눠서 Gpu에 분할 로드
    - PcdStreamingController에 Gpu 성능에 따라 조작할 수 있도록 Inspecter에 표시
        
        -> Build 시 사용 가능하도록 UI 제작까지가 목표
- Point 자체 사이즈 조절 UI만 구현 (Key Binding : F1)

</details>

<details>
<summary>#3</summary>

20250825 ~ 20250827
- EDL(Eye-Dome Lighting) 구현 -> Camera에서 표현되는 그래픽 옵션
- LOD(Level Of Detail/Depth) 구현 -> 깊이에 따라 색상이 다르게 보이는 그래픽 옵션
- Point Sizing 구현 -> Adaptive, Fixed, Attenuation
    - Point 크기를 어떤 기준으로 정할지 선택 (예: 화면 기준, 카메라 기준 등)

</details>

<details>
<summary>#4</summary>

20250828 ~ 20250901
- 계층별 색상 표현 구현
- MRT 누적 기법 사용 (Accum, Normalize)
- 깊이에 따른 색상 표현 변경 -> 카메라 기준으로 거리에 따라 색 변경

</details>

## 참고
[Potree_Github]https://github.com/potree/potree