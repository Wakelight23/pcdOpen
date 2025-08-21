# PCD OPEN (with UNITY)

## Description

Unity엔진으로 Point Cloud Data를 화면에 표시합니다.

PCD를 확인하기 위해서는 .pcd 확장자의 파일이 필요합니다.

## 구현내용
- 2GB 미만 .pcd 확장자 파일을 Unity에서 Load
- Load한 Point Cloud Data 조작 (마우스)
- Build 대응 간단한 UI 제작
- 메모리, 성능 확인용 Assets 'Graphy' 포함 (Key binding : F12)

## Patch Note (ver 0.0.2)
- Octree 알고리즘 적용, 저사양 PC에서 불러오기가 가능합니다.
- PC 성능에 따라 로드 옵션 설정 가능 (Only Editor)
- 2GB 이상 .pcd 파일 로드 가능합니다.

### Notice
- 용량이 높아질수록 불러오기 시간이 길어집니다.