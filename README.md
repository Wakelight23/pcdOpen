# PCD OPEN (with UNITY)

## Description

Unity엔진으로 Point Cloud Data를 화면에 표시합니다.

PCD를 확인하기 위해서는 .pcd 확장자의 파일이 필요합니다.

## 구현내용
- 2GB 미만 .pcd 확장자 파일을 Unity에서 Load
- Load한 Point Cloud Data 조작 (마우스)
- Build 대응 간단한 UI 제작
- 메모리, 성능 확인용 Assets 'Graphy' 포함 (Key binding : F12)

## How To Use (ver 0.0.1)

### Unity Editor
1. Editor를 실행 후 Hierarchy에서 PCDManager를 선택합니다.
2. Inspector에서 ‘PcdEntry’ Script를 우클릭합니다.
3. 메뉴(Context Menu) 중 맨 아래에 있는 ‘Load PCD (Editor)’를 선택합니다
4. 로컬에서 화면에 표시하길 원하는 파일을 찾아 선택 후 불러옵니다.

### Notice
- 2GB 이상 파일은 불러올 수 없습니다.