Shader "AudioLink/Debug/AudioLinkTestTime"
{
    Properties
    {
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100

        Pass
        {
            CGPROGRAM

            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"
            #include "AudioLink.cginc"
            #include "SmoothPixelFont.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_OUTPUT_STEREO
            };


            v2f vert (appdata v)
            {
                v2f o;

                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                return o;
            }

            float4 frag (v2f i) : SV_Target
            {
                float value = 0;
                
                float2 iuv = i.uv;
                iuv.y = 1.-iuv.y;
                const uint rows = 21;
                const uint cols = 21;
                const uint number_area_cols = 11;
                
                float2 pos = iuv*float2(cols,rows);
                uint2 dig = (uint2)(pos);

                // This line of code is tricky;  We determine how much we should soften the edge of the text
                // based on how quickly the text is moving across our field of view.  This gives us realy nice
                // anti-aliased edges.
                float2 softness = 2./pow( length( float2( ddx( pos.x ), ddy( pos.y ) ) ), 0.5 );

                // Another option would be to set softness to 20 and YOLO it.

                float2 fmxy = float2( 4, 6 ) - (glsl_mod(pos,1.)*float2(4.,6.));

                value = 0;
                int xoffset = 5;
                bool leadingzero = false;
                int points_after_decimal = 0; 
                int max_decimals = 6;

                if( dig.y < 13 )
                {
                    if( dig.x < cols - number_area_cols )
                    {
                        uint sendchar = 0;
                        const uint sendarr[130] = { 
                            'I', 'n', 's', 't', 'a', 'n', 'c', 'e', ' ', ' ',
                            'W', 'a', 'l', 'l', 'c', 'l', 'o', 'c', 'k', ' ',
                            'N', 'e', 't', 'w', 'o', 'r', 'k', ' ', ' ', ' ',
                            'U', 'T', 'C', ' ', 'D', 'a', 'y', 's', ' ', ' ',
                            'U', 'T', 'C', ' ', 'S', 'e', 'c', 'o', 'n', 'd',
                            'A', 'u', 't', 'o', ' ', 'g', 'a', 'i', 'n', ' ',
                            'P', 'e', 'a', 'k', ' ', 'v', 'a', 'l', 'u', 'e',
                            'R', 'M', 'S', ' ', 'v', 'a', 'l', 'u', 'e', ' ',
                            'F', 'P', 'S', ' ', 'T', '/', 'A', 'L', ' ', ' ',
                            'P', 'l', 'a', 'y', 'e', 'r', 'I', 'n', 'f', 'o',
                            'D', 'e', 'b', 'u', 'g', ' ', '1', ' ', ' ', ' ',
                            'D', 'e', 'b', 'u', 'g', ' ', '2', ' ', ' ', ' ',
                            'D', 'e', 'b', 'u', 'g', ' ', '3', ' ', ' ', ' ',
                            };
                        sendchar = sendarr[dig.x+dig.y*10];
                        return PrintChar( sendchar, fmxy, softness, 0.0 );
                    }
                    
                    dig.x -= cols - number_area_cols;
                }
                else
                {
                    //Colorchord territory debugging.
                }

                switch( dig.y )
                {
                case 0:
                case 1:
                    // 2: Time since level start in milliseconds.
                    // 3: Time of day.
                    value = AudioLinkDecodeDataAsSeconds( dig.y?ALPASS_GENERALVU_LOCAL_TIME:ALPASS_GENERALVU_INSTANCE_TIME );
                    float seconds = glsl_mod(value, 60);
                    int minutes = (value/60) % 60;
                    int hours = (value/3600);
                    value = hours * 10000 + minutes * 100 + seconds;
                    
                    if( dig.x < 3 )
                    {
                        value = hours;
                        xoffset = 2;
                        leadingzero = 1;
                    }
                    else if( dig.x < 5 )
                    {
                        value = minutes;
                        xoffset = 5;
                        leadingzero = 1;
                    }
                    else if( dig.x > 5)
                    {
                        value = seconds;
                        xoffset = 8;
                        leadingzero = 1;
                    }
                    break;
                case 2:
                    if( dig.x < 8 )
                    {
                        value = AudioLinkDecodeDataAsUInt( ALPASS_GENERALVU_NETWORK_TIME )/1000;
                        xoffset = 7;
                    }
                    else
                    {
                        value = AudioLinkDecodeDataAsUInt( ALPASS_GENERALVU_NETWORK_TIME )%1000;
                        xoffset = 11;
                        leadingzero = 1;
                    }
                    break;
                case 3:
					value = AudioLinkDecodeDataAsUInt( ALPASS_GENERALVU_UNIX_DAYS );
					xoffset = 11;
					break;
                case 4:
					if( dig.x < 8 )
					{
						value = AudioLinkDecodeDataAsUInt( ALPASS_GENERALVU_UNIX_SECONDS )/1000;
						xoffset = 7;
					}
					else
					{
						value = AudioLinkDecodeDataAsUInt( ALPASS_GENERALVU_UNIX_SECONDS )%1000;
						xoffset = 11;
						leadingzero = 1;
					}
					break;
                case 5:
                    value = AudioLinkData( int2( ALPASS_GENERALVU + int2( 11, 0 ) ) ); //Autogain Debug
                    break;
                case 6:
                    value = AudioLinkData( int2( ALPASS_GENERALVU + int2( 8, 0 ) ) ).y; //Peak
                    break;
                case 7:
                    value = AudioLinkData( int2( ALPASS_GENERALVU + int2( 8, 0 ) ) ).x; //RMS
                    break;

                case 8:
                    if( dig.x < 7 )
                    {
                        value = AudioLinkData( int2( ALPASS_GENERALVU + int2( 0, 0 ) ) ).b; //True FPS
                        xoffset = 7;
                    }
                    else
                    {
                        value = AudioLinkData( int2( ALPASS_GENERALVU + int2( 1, 0 ) ) ).b; //AudioLink FPS
                        xoffset = 11;
                    }
                    break;

                case 9:
                    if( dig.x < 3 )
                    {
                        value = AudioLinkData( int2( ALPASS_GENERALVU_PLAYERINFO ) ).r;
                        xoffset = 3;
                    }
                    else if( dig.x < 9 )
                    {
                        value = AudioLinkData( int2( ALPASS_GENERALVU_PLAYERINFO ) ).g;
                        xoffset = 9;
                    }
                    else
                    {
                        value = AudioLinkData( int2( ALPASS_GENERALVU_PLAYERINFO ) ).b;
                        xoffset = 11;
                    }
                    break;
                case 10:
                    //GENERAL DEBUG VALUE 1
                    value = AudioLinkData( int2( ALPASS_GENERALVU + int2(7, 0 ) ) ).x;
                    break;
                case 11:
                    //GENERAL DEBUG VALUE 2
                    value = AudioLinkData( int2( ALPASS_GENERALVU + int2(7, 0 ) ) ).y;
                    break;
                case 12:
                    //GENERAL DEBUG VALUE 3
                    value = AudioLinkData( int2( ALPASS_GENERALVU + int2(7, 0 ) ) ).z;
                    break;
                default:
                    if( dig.x < 5 )
                    {
                        // CC Note
                        value = AudioLinkData( ALPASS_CCINTERNAL + int2( 1 + dig.y - 13, 0 ) ).x;
                        xoffset = 2;
                    }
                    else if( dig.x < 10 )
                    {
                        //CC Note Number
                        value = AudioLinkData( ALPASS_CCINTERNAL + int2( 1 + dig.y - 13, 1 ) ).x;
                        xoffset = 10;
                    }
                    else if( dig.x < 15 )
                    {
                        //Time Existed
                        value = AudioLinkData( ALPASS_CCINTERNAL + int2( 1 + dig.y - 13, 1 ) ).y;
                        xoffset = 13;
                    }
                    else if( dig.x < 20 )
                    {
                        //Intensity
                        xoffset = 18;
                        value = AudioLinkData( ALPASS_CCINTERNAL + int2( 1 + dig.y - 13, 0 ) ).a;
                    }
                    break;
                }

                return PrintNumberOnLine( value, fmxy, softness, dig.x - xoffset, points_after_decimal, max_decimals, leadingzero, 0 );         
            }
            ENDCG
        }
    }
}
