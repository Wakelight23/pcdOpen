# PCD OPEN (with UNITY)

## Description

Unity엔진으로 Point Cloud Data를 화면에 표시합니다.

이 Viewer는 .pcd 확장자 데이터를 확인하기 위해 제작되었습니다.

## Patch Note (ver 0.0.3)
- 그래픽 최적화
	- Shader 변경 (PcdPoint : 1픽셀 표현 -> PcdBillboard : Quard 표현)
	- Point 크기 변경 가능
	- GPU 옵션 UI 추가 (Key Binding : F1)
	- EDL, LOD 추가
	- Alpha Blending 추가
	- Point Sizing 추가 (Adaptive, Fixed, Attenuation)
- Camera 조작 방법 수정 (표준 PcdViewer와 유사하게 작동하도록 수정)

### Notice
- 용량이 높아질수록 불러오기 시간이 길어집니다.
- Point Budget을 높일수록 그래픽 로드율이 높아집니다.
- Point Budget을 낮추면 저사양에서도 쾌적하게 사용할 수 있지만 심미성이 떨어집니다.
- Point 자체 크기(Point Size)를 키우면 그래픽 로드율이 높아집니다.

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

20250825 ~ 
- EDL(Eye-Dome Lighting) 구현 -> Camera에서 표현되는 그래픽 옵션
- LOD(Level Of Detail/Depth) 구현 -> 깊이에 따라 색상이 다르게 보이는 그래픽 옵션
- Point Sizing 구현 -> Adaptive, Fixed, Attenuation
    - Point 크기를 어떤 기준으로 정할지 선택 (예: 화면 기준, 카메라 기준 등)
- 

</details>

## 참고
[Potree_Github]https://github.com/potree/potree