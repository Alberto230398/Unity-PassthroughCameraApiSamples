Shader "Custom/DepthSobel"
{
    // Sobel edge detection sulla DEPTH GIÀ ALLINEATA (output di Custom/AlignedDepthExport):
    // texture float in cui ogni canale contiene la depth metrica in metri, e 0 = pixel
    // senza dato depth. Va usato con Graphics.Blit(alignedDepthTex, rt, sobelMaterial),
    // con rt in formato float (RenderTextureFormat.ARGBFloat) per non perdere i gradienti.
    Properties
    {
        _MainTex("Aligned Depth (metric, R = meters)", 2D) = "black" {}
        // Guadagno applicato alla magnitudine del gradiente prima dell'output.
        // 1 = metri/texel grezzi; alza per visualizzare bordi deboli.
        _EdgeScale("Edge Scale", Float) = 1.0
        // Salto di depth (in metri) oltre il quale due texel adiacenti sono
        // considerati discontinui: i bordi sopra questa soglia vengono ignorati
        // così il contorno di un oggetto vicino/lontano non satura il filtro.
        _DepthDiscontinuity("Depth Discontinuity (m)", Float) = 0.5
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Cull Off ZWrite Off ZTest Always
        LOD 200

        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            // xy = 1/width,1/height  zw = width,height. Riempito automaticamente da Unity.
            float4 _MainTex_TexelSize;
            float _EdgeScale;
            float _DepthDiscontinuity;

            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert(appdata_img v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord;
                return o;
            }

            // Legge la depth metrica (canale R) al texel offset (dx,dy) dal centro.
            // Ritorna la depth del centro per i pixel invalidi (0) o per salti troppo
            // grandi: così i buchi nella depth e i bordi di occlusione non generano
            // gradienti spuri enormi. `valid` segnala se il campione era utilizzabile.
            float SampleDepth(float2 uv, float2 offset, float center, out float valid)
            {
                float d = tex2D(_MainTex, uv + offset * _MainTex_TexelSize.xy).r;
                valid = (d > 0.0001) ? 1.0 : 0.0;
                if (valid < 0.5) return center;
                if (abs(d - center) > _DepthDiscontinuity) { valid = 0.0; return center; }
                return d;
            }

            float4 frag(v2f i) : SV_Target
            {
                // Il centro deve avere dato depth, altrimenti niente bordo qui.
                float center = tex2D(_MainTex, i.uv).r;
                if (center <= 0.0001)
                    return float4(0.0, 0.0, 0.0, 1.0);

                float v;
                // Kernel Sobel 3x3 sui valori di depth (in metri).
                float tl = SampleDepth(i.uv, float2(-1, -1), center, v);
                float tc = SampleDepth(i.uv, float2( 0, -1), center, v);
                float tr = SampleDepth(i.uv, float2( 1, -1), center, v);
                float ml = SampleDepth(i.uv, float2(-1,  0), center, v);
                float mr = SampleDepth(i.uv, float2( 1,  0), center, v);
                float bl = SampleDepth(i.uv, float2(-1,  1), center, v);
                float bc = SampleDepth(i.uv, float2( 0,  1), center, v);
                float br = SampleDepth(i.uv, float2( 1,  1), center, v);

                // Gradiente orizzontale e verticale (metri per texel).
                float gx = (tl + 2.0 * ml + bl) - (tr + 2.0 * mr + br);
                float gy = (tl + 2.0 * tc + tr) - (bl + 2.0 * bc + br);

                float edge = sqrt(gx * gx + gy * gy) * _EdgeScale;

                // Magnitudine del gradiente in tutti i canali: leggibile sia come
                // immagine (grayscale) sia come valore metrico da qualsiasi canale.
                return float4(edge, edge, edge, 1.0);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
