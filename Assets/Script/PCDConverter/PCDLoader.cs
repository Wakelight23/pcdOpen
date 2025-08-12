using System;
using System.IO;
using System.Text;
using System.Globalization;
using System.Collections.Generic;
using UnityEngine;

public class PcdData
{
    public Vector3[] positions;
    public Color32[] colors;     // rgb/rgba�� ���� ����
    public float[] intensity;    // intensity�� ���� ����
    public int pointCount;
}

public static class PcdLoader
{
    public static PcdData LoadFromFile(string path)
    {
        // ��ü ���� ����Ʈ
        byte[] all = File.ReadAllBytes(path);

        // ����� ASCII�� �� ���� �Ľ� ����
        int headerEnd = FindHeaderEnd(all); // "DATA ascii/binary/..." ������ ������ ���� �� ���� offset ��ȯ
        if (headerEnd <= 0)
            throw new Exception("PCD header not found");

        string headerText = Encoding.ASCII.GetString(all, 0, headerEnd);
        var header = ParseHeader(headerText);

        // �ʵ� �ε��� Ȯ��
        int ix = Array.IndexOf(header.FIELDS, "x");
        int iy = Array.IndexOf(header.FIELDS, "y");
        int iz = Array.IndexOf(header.FIELDS, "z");
        int iRgb = Array.IndexOf(header.FIELDS, "rgb");
        int iRgba = Array.IndexOf(header.FIELDS, "rgba");
        int iIntensity = Array.IndexOf(header.FIELDS, "intensity");
        if (ix < 0 || iy < 0 || iz < 0)
            throw new Exception("x/y/z fields are required");

        int points = header.POINTS > 0 ? header.POINTS : header.WIDTH * Math.Max(1, header.HEIGHT);
        var data = new PcdData { pointCount = points, positions = new Vector3[points] };

        if (iRgb >= 0 || iRgba >= 0) data.colors = new Color32[points];
        if (iIntensity >= 0) data.intensity = new float[points];

        switch (header.DATA)
        {
            case "ascii":
                ParseAscii(all, headerEnd, header, ix, iy, iz, iRgb, iRgba, iIntensity, data);
                break;
            case "binary":
                ParseBinary(all, headerEnd, header, ix, iy, iz, iRgb, iRgba, iIntensity, data, false);
                break;
            case "binary_compressed":
                // binary_compressed�� �߰��� ����ũ��/����ũ��� zlib blob�� ���Ե�.
                // ���� ������ zlib inflate �ʿ�(��: Ionic.Zlib ��). ���⼭�� ���� ó��.
                throw new NotSupportedException("binary_compressed not implemented in this sample");
            default:
                throw new Exception("Unsupported DATA type: " + header.DATA);
        }

        return data;
    }

    class Header
    {
        public string VERSION;
        public string[] FIELDS;
        public int[] SIZE;
        public char[] TYPE; // F, I, U
        public int[] COUNT;
        public int WIDTH = 0;
        public int HEIGHT = 1;
        public int POINTS = 0;
        public string DATA;
        public int FieldCount => FIELDS?.Length ?? 0;
    }

    static int FindHeaderEnd(byte[] bytes)
    {
        // ����� "DATA ..." �ٷ� ������ ���� �ٺ��� ������
        // '\n' ���� �˻�
        string ascii = Encoding.ASCII.GetString(bytes);
        int dataIdx = ascii.IndexOf("\nDATA ");
        if (dataIdx < 0)
            dataIdx = ascii.IndexOf("\r\nDATA ");
        if (dataIdx < 0) return -1;

        // DATA �� ��
        int lineEnd = ascii.IndexOf('\n', dataIdx + 1);
        if (lineEnd < 0) lineEnd = ascii.Length;
        // ������ ���� offset(����Ʈ ����) ��ȯ
        return Encoding.ASCII.GetByteCount(ascii.Substring(0, lineEnd + 1));
    }

    static Header ParseHeader(string header)
    {
        var h = new Header();
        var lines = header.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("#")) continue;
            if (line.StartsWith("VERSION")) h.VERSION = line.Substring("VERSION".Length).Trim();
            else if (line.StartsWith("FIELDS"))
                h.FIELDS = SplitTokens(line.Substring("FIELDS".Length));
            else if (line.StartsWith("SIZE"))
                h.SIZE = Array.ConvertAll(SplitTokens(line.Substring("SIZE".Length)), int.Parse);
            else if (line.StartsWith("TYPE"))
            {
                var tokens = SplitTokens(line.Substring("TYPE".Length));
                h.TYPE = Array.ConvertAll(tokens, t => t[0]); // 'F','I','U'
            }
            else if (line.StartsWith("COUNT"))
                h.COUNT = Array.ConvertAll(SplitTokens(line.Substring("COUNT".Length)), int.Parse);
            else if (line.StartsWith("WIDTH"))
                h.WIDTH = int.Parse(line.Substring("WIDTH".Length));
            else if (line.StartsWith("HEIGHT"))
                h.HEIGHT = int.Parse(line.Substring("HEIGHT".Length));
            else if (line.StartsWith("POINTS"))
                h.POINTS = int.Parse(line.Substring("POINTS".Length));
            else if (line.StartsWith("DATA"))
                h.DATA = line.Substring("DATA".Length).Trim().ToLowerInvariant();
        }
        // �⺻�� ����
        if (h.COUNT == null && h.FIELDS != null)
        {
            h.COUNT = new int[h.FIELDS.Length];
            for (int i = 0; i < h.COUNT.Length; i++) h.COUNT[i] = 1;
        }
        return h;
    }

    static string[] SplitTokens(string s)
    {
        return s.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
    }

    static void ParseAscii(byte[] all, int dataOffset, Header h,
                           int ix, int iy, int iz, int iRgb, int iRgba, int iIntensity,
                           PcdData outData)
    {
        string body = Encoding.ASCII.GetString(all, dataOffset, all.Length - dataOffset);
        var lines = body.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

        var numberFormat = CultureInfo.InvariantCulture;
        int idx = 0;
        foreach (var line in lines)
        {
            if (idx >= outData.pointCount) break;
            var tok = SplitTokens(line);
            // Ȯ��: COUNT>1 �ʵ�� ���� ó�� �ʿ�. ���⼭�� �Ϲ� ���� ��Į�� �ʵ� ����.
            float fx = float.Parse(tok[ix], numberFormat);
            float fy = float.Parse(tok[iy], numberFormat);
            float fz = float.Parse(tok[iz], numberFormat);
            outData.positions[idx] = new Vector3(fx, fy, fz);

            if (outData.colors != null)
            {
                if (iRgb >= 0)
                    outData.colors[idx] = DecodeRgb(tok[iRgb], numberFormat);
                else if (iRgba >= 0)
                    outData.colors[idx] = DecodeRgba(tok[iRgba], numberFormat);
            }
            if (outData.intensity != null && iIntensity >= 0)
                outData.intensity[idx] = float.Parse(tok[iIntensity], numberFormat);

            idx++;
        }
    }

    static Color32 DecodeRgb(string token, IFormatProvider fmt)
    {
        // PCD rgb�� float32�� R,G,B packed�� ��찡 ����. ASCII������ ������ float�� ǥ���� �� ����.
        // �켱 float�� �Ľ� �� ��Ʈĳ���� �õ�, ���н� ������ ����.
        if (float.TryParse(token, NumberStyles.Float, fmt, out float f))
        {
            uint u = BitConverter.ToUInt32(BitConverter.GetBytes(f), 0);
            byte r = (byte)((u >> 16) & 0xFF);
            byte g = (byte)((u >> 8) & 0xFF);
            byte b = (byte)(u & 0xFF);
            return new Color32(r, g, b, 255);
        }
        else
        {
            uint u = Convert.ToUInt32(token);
            byte r = (byte)((u >> 16) & 0xFF);
            byte g = (byte)((u >> 8) & 0xFF);
            byte b = (byte)(u & 0xFF);
            return new Color32(r, g, b, 255);
        }
    }

    static Color32 DecodeRgba(string token, IFormatProvider fmt)
    {
        if (float.TryParse(token, NumberStyles.Float, fmt, out float f))
        {
            uint u = BitConverter.ToUInt32(BitConverter.GetBytes(f), 0);
            byte r = (byte)((u >> 16) & 0xFF);
            byte g = (byte)((u >> 8) & 0xFF);
            byte b = (byte)(u & 0xFF);
            byte a = (byte)((u >> 24) & 0xFF);
            return new Color32(r, g, b, a);
        }
        else
        {
            uint u = Convert.ToUInt32(token);
            byte r = (byte)((u >> 16) & 0xFF);
            byte g = (byte)((u >> 8) & 0xFF);
            byte b = (byte)(u & 0xFF);
            byte a = (byte)((u >> 24) & 0xFF);
            return new Color32(r, g, b, a);
        }
    }

    static void ParseBinary(byte[] all, int dataOffset, Header h,
                            int ix, int iy, int iz, int iRgb, int iRgba, int iIntensity,
                            PcdData outData, bool littleEndian = true)
    {
        // �� ����Ʈ�� ����Ʈ ũ�� ���
        int pointStride = 0;
        for (int i = 0; i < h.FieldCount; i++)
            pointStride += h.SIZE[i] * h.COUNT[i];

        // �� �ʵ��� ������
        int[] fieldOffset = new int[h.FieldCount];
        {
            int off = 0;
            for (int i = 0; i < h.FieldCount; i++)
            {
                fieldOffset[i] = off;
                off += h.SIZE[i] * h.COUNT[i];
            }
        }

        int pcount = outData.pointCount;
        for (int p = 0; p < pcount; p++)
        {
            int baseOff = dataOffset + p * pointStride;

            float fx = ReadFloat(all, baseOff + fieldOffset[ix], littleEndian);
            float fy = ReadFloat(all, baseOff + fieldOffset[iy], littleEndian);
            float fz = ReadFloat(all, baseOff + fieldOffset[iz], littleEndian);
            outData.positions[p] = new Vector3(fx, fy, fz);

            if (outData.colors != null)
            {
                if (iRgb >= 0)
                {
                    uint u = ReadUInt32(all, baseOff + fieldOffset[iRgb], littleEndian);
                    byte r = (byte)((u >> 16) & 0xFF);
                    byte g = (byte)((u >> 8) & 0xFF);
                    byte b = (byte)(u & 0xFF);
                    outData.colors[p] = new Color32(r, g, b, 255);
                }
                else if (iRgba >= 0)
                {
                    uint u = ReadUInt32(all, baseOff + fieldOffset[iRgba], littleEndian);
                    byte r = (byte)((u >> 16) & 0xFF);
                    byte g = (byte)((u >> 8) & 0xFF);
                    byte b = (byte)(u & 0xFF);
                    byte a = (byte)((u >> 24) & 0xFF);
                    outData.colors[p] = new Color32(r, g, b, a);
                }
            }

            if (outData.intensity != null && iIntensity >= 0)
            {
                // TYPE/size Ȯ���� float/int ���������� �д� ó���� �� ��Ȯ�� �� �� ����
                outData.intensity[p] = ReadFloat(all, baseOff + fieldOffset[iIntensity], littleEndian);
            }
        }
    }

    static float ReadFloat(byte[] buf, int off, bool le)
    {
        if (le)
            return BitConverter.ToSingle(buf, off);
        // BE ó�� �ʿ� ��:
        var tmp = new byte[4];
        Buffer.BlockCopy(buf, off, tmp, 0, 4);
        Array.Reverse(tmp);
        return BitConverter.ToSingle(tmp, 0);
    }

    static uint ReadUInt32(byte[] buf, int off, bool le)
    {
        if (le)
            return BitConverter.ToUInt32(buf, off);
        var tmp = new byte[1];
        Buffer.BlockCopy(buf, off, tmp, 0, 4);
        Array.Reverse(tmp);
        return BitConverter.ToUInt32(tmp, 0);
    }
}
