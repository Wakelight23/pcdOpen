# PCD OPEN (with UNITY)

## Description

Unity엔진으로 Point Cloud Data를 화면에 표시합니다.

PCD를 확인하기 위해서는 .pcd 확장자의 파일이 필요합니다.

## Patch Note (ver 0.0.2)
- Octree 알고리즘 적용, 저사양 PC에서 불러오기가 가능합니다.
- PC 성능에 따라 로드 옵션 설정 가능 (Only Editor)
- 2GB 이상 .pcd 파일 로드 가능합니다.

### Notice
- 용량이 높아질수록 불러오기 시간이 길어집니다.

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

20250820 ~
- 2GB 이상 .pcd 확장자 파일 불러오기 가능 (PC 사양에 따라 차이가 있음)
- Gpu 일괄 렌더링 -> Octree 알고리즘 렌더링 로직 변경 (참고 : Potree)
    - GPU에 한 번에 전부 올리는 것이 아닌 'chunk' 단위로 나눠서 Gpu에 분할 로드
    - PcdStreamingController에 Gpu 성능에 따라 조작할 수 있도록 Inspecter에 표시
        
        -> Build 시 사용 가능하도록 UI 제작까지가 목표

</details>