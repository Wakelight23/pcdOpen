using System;
using UnityEngine;

public static class PcdBoxFilter
{
    // 설정값: 레벨별 타깃에 맞춰 동적으로 셀 분할 결정
    public struct Settings
    {
        public int targetPoints;     // 노드 타깃 포인트 수(예: 루트 200_000, 레벨당 1/2 감쇠)
        public int minGrid;          // 각 축 최소 셀 수(예: 4)
        public int maxGrid;          // 각 축 최대 셀 수(예: 64)
        public float occupancyBias;  // 셀 채움 허용치 보정(0.8~1.2), 1=기본
        public bool preferCenter;    // 대표점 선택 시 셀 중심 근접 우선
        public bool averageColor;    // 대표점 색을 셀 평균색으로 대체
    }

    public static void DownsampleBox(
        in Bounds nodeBounds,
        Vector3[] positions,
        Color32[] colors,
        in Settings opt,
        out Vector3[] outPositions,
        out Color32[] outColors)
    {
        if (positions == null || positions.Length == 0)
        {
            outPositions = Array.Empty<Vector3>();
            outColors = colors != null ? Array.Empty<Color32>() : null;
            return;
        }

        // 1) 그리드 해상도 결정: 노드 크기와 타깃 개수 기반 근사
        //    셀 수(px*py* pz)가 대략 targetPoints에 근접하도록 정함.
        int grid = EstimateGridResolution(opt.targetPoints, positions.Length, opt.minGrid, opt.maxGrid, opt.occupancyBias);
        int px = grid, py = grid, pz = grid;

        Vector3 min = nodeBounds.min;
        Vector3 size = nodeBounds.size;
        Vector3 cell = new Vector3(
            Mathf.Max(1e-6f, size.x / px),
            Mathf.Max(1e-6f, size.y / py),
            Mathf.Max(1e-6f, size.z / pz)
        );

        int cellCount = px * py * pz;

        // 2) 셀 누적 버퍼
        var repIndex = new int[cellCount];         // 대표점 인덱스
        var count = new int[cellCount];            // 셀 포인트 수
        var sumR = new double[cellCount];
        var sumG = new double[cellCount];
        var sumB = new double[cellCount];

        // 셀 중심 캐시(대표 선택 보조)
        var centers = new Vector3[cellCount];
        {
            int idx = 0;
            for (int iz = 0; iz < pz; iz++)
                for (int iy = 0; iy < py; iy++)
                    for (int ix = 0; ix < px; ix++, idx++)
                    {
                        var cmin = new Vector3(min.x + ix * cell.x, min.y + iy * cell.y, min.z + iz * cell.z);
                        centers[idx] = cmin + 0.5f * cell;
                    }
        }

        // 3) 포인트 → 셀 binning + 대표 선택 + 색 누적
        for (int i = 0; i < positions.Length; i++)
        {
            var p = positions[i];
            int ix = Mathf.Clamp(Mathf.FloorToInt((p.x - min.x) / Mathf.Max(1e-6f, size.x) * px), 0, px - 1);
            int iy = Mathf.Clamp(Mathf.FloorToInt((p.y - min.y) / Mathf.Max(1e-6f, size.y) * py), 0, py - 1);
            int iz = Mathf.Clamp(Mathf.FloorToInt((p.z - min.z) / Mathf.Max(1e-6f, size.z) * pz), 0, pz - 1);
            int ci = (iz * py + iy) * px + ix;

            // 대표 선택: preferCenter면 중심과의 거리 최소, 아니면 첫 점
            if (count[ci] == 0)
            {
                repIndex[ci] = i;
            }
            else if (opt.preferCenter)
            {
                var curIdx = repIndex[ci];
                float dCur = (positions[curIdx] - centers[ci]).sqrMagnitude;
                float dNew = (p - centers[ci]).sqrMagnitude;
                if (dNew < dCur) repIndex[ci] = i;
            }

            count[ci]++;

            if (colors != null && i < colors.Length)
            {
                // sRGB -> Linear 누적(간단 근사)
                var c8 = colors[i];
                var lin = new Vector3(GammaToLinear01(c8.r / 255f), GammaToLinear01(c8.g / 255f), GammaToLinear01(c8.b / 255f));
                sumR[ci] += lin.x; sumG[ci] += lin.y; sumB[ci] += lin.z;
            }
        }

        // 4) 출력 배열 구축
        //    타깃 근사: 채워진 셀 수 ~= 출력 포인트 수
        int filled = 0;
        for (int c = 0; c < cellCount; c++) if (count[c] > 0) filled++;
        // 타깃에 너무 벗어나면 간단 다운/업 샘플 보정 가능(여기선 filled 그대로)
        outPositions = new Vector3[filled];
        outColors = (colors != null) ? new Color32[filled] : null;

        int w = 0;
        for (int c = 0; c < cellCount; c++)
        {
            int n = count[c];
            if (n <= 0) continue;

            int ri = Mathf.Clamp(repIndex[c], 0, positions.Length - 1);
            var rp = positions[ri];
            outPositions[w] = rp;

            if (outColors != null)
            {
                if (opt.averageColor && n > 0)
                {
                    // 평균 리니어 -> sRGB
                    var avgLin = new Vector3((float)(sumR[c] / n), (float)(sumG[c] / n), (float)(sumB[c] / n));
                    byte r = (byte)Mathf.Clamp(Mathf.RoundToInt(LinearToGamma01(avgLin.x) * 255f), 0, 255);
                    byte g = (byte)Mathf.Clamp(Mathf.RoundToInt(LinearToGamma01(avgLin.y) * 255f), 0, 255);
                    byte b = (byte)Mathf.Clamp(Mathf.RoundToInt(LinearToGamma01(avgLin.z) * 255f), 0, 255);
                    outColors[w] = new Color32(r, g, b, 255);
                }
                else
                {
                    // 대표점의 원본 색 사용
                    outColors[w] = (ri < colors.Length) ? colors[ri] : new Color32(255, 255, 255, 255);
                }
            }
            w++;
        }
    }

    static int EstimateGridResolution(int target, int sourceCount, int minGrid, int maxGrid, float bias)
    {
        // 목표: grid^3 ≈ target. 다만 source가 너무 적으면 grid 축소
        if (target <= 0) target = Mathf.Max(1, sourceCount / 4);
        float g = Mathf.Pow(Mathf.Max(1, target) * Mathf.Clamp(bias, 0.5f, 2.0f), 1f / 3f);
        int grid = Mathf.Clamp(Mathf.RoundToInt(g), Mathf.Max(1, minGrid), Mathf.Max(minGrid, maxGrid));
        // 너무 많은 셀은 의미 없으므로 소스 수 대비 상한
        int maxBySrc = Mathf.Max(1, Mathf.RoundToInt(Mathf.Pow(Mathf.Max(1, sourceCount), 1f / 3f)));
        return Mathf.Min(grid, maxBySrc);
    }

    static float GammaToLinear01(float c) { return Mathf.Approximately(c, 0f) ? 0f : Mathf.Pow(c, 2.2f); }
    static float LinearToGamma01(float c) { return c <= 0f ? 0f : Mathf.Pow(c, 1f / 2.2f); }
}
