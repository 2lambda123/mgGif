//#define mgGIF_UNSAFE

using UnityEngine;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace MG.GIF
{
    ////////////////////////////////////////////////////////////////////////////////

    public enum Disposal
    {
        None              = 0x00,
        DoNotDispose      = 0x04,
        RestoreBackground = 0x08,
        ReturnToPrevious  = 0x0C
    }

    ////////////////////////////////////////////////////////////////////////////////
    
    public class Image
    {
        public int       Width;
        public int       Height;
        public int       Delay; // milliseconds
        public Color32[] RawImage;

        public Texture2D CreateTexture()
        {
            var tex = new Texture2D( Width, Height, TextureFormat.ARGB32, false );

            tex.filterMode = FilterMode.Point;
            tex.wrapMode   = TextureWrapMode.Clamp;

            tex.SetPixels32( RawImage );
            tex.Apply();

            return tex;
        }
    }


    ////////////////////////////////////////////////////////////////////////////////

    public class ImageList
    {
        public string Version;
        public ushort Width;
        public ushort Height;

        public List<Image> Images = new List<Image>();

        public void Add( Image img )
        {
            Images.Add( img );
        }

        public Image GetImage( int index )
        {
            return index < Images.Count ? Images[index] : null;
        }

        public int NumFrames
        {
            get
            {
                int count = 0;

                foreach( var img in Images )
                {
                    if( img.Delay > 0 )
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        public Image GetFrame( int index )
        {
            if( Images.Count == 0 )
            {
                return null;
            }

            foreach( var img in Images )
            {
                if( img.Delay > 0 )
                {
                    if( index == 0 )
                    {
                        return img;
                    }

                    index--;
                }
            }

            return Images[Images.Count - 1];
        }
    }


    ////////////////////////////////////////////////////////////////////////////////

    public class Decoder
    {
        [Flags]
        private enum ImageFlag
        {
            Interlaced      = 0x40,
            ColourTable     = 0x80,
            TableSizeMask   = 0x07,
            BitDepthMask    = 0x70,
        }

        private enum Block
        {
            Image           = 0x2C,
            Extension       = 0x21,
            End             = 0x3B
        }

        private enum Extension
        {
            GraphicControl  = 0xF9,
            Comments        = 0xFE,
            PlainText       = 0x01,
            ApplicationData = 0xFF
        }

        const uint          NoCode              = 0xFFFF;
        const ushort        NoTransparency      = 0xFFFF;

        private ImageList   Images;
        private Color32[]   LastImage           = null;

        // colour
        private Color32[]   GlobalColourTable   = new Color32[ 4096 ];
        private Color32[]   LocalColourTable    = new Color32[ 4096 ];
        private Color32[]   ActiveColourTable   = null;
        private ushort      TransparentIndex    = NoTransparency;

        // current controls
        private ushort      ControlDelay        = 0;
        private Disposal    ControlDispose      = Disposal.None;

        // global image
        private ushort      GlobalWidth;
        private ushort      GlobalHeight;

        // current image
        private ushort      ImageLeft;
        private ushort      ImageTop;
        private ushort      ImageWidth;
        private ushort      ImageHeight;

        //------------------------------------------------------------------------------
        // data

        byte[]  Data;
        int     D;

        public Decoder( byte[] data )
        {
            Data = data;
            D    = 0;
        }

        [MethodImpl( MethodImplOptions.AggressiveInlining )]
        byte ReadByte()
        {
            return Data[ D++ ];
        }

        [MethodImpl( MethodImplOptions.AggressiveInlining )]
        ushort ReadUInt16()
        {
            return (ushort) ( Data[ D++ ] | Data[ D++ ] << 8 );
        }

        //------------------------------------------------------------------------------

        public static ImageList Parse( byte[] data )
        {
            return new Decoder( data ).Decode();
        }

        //------------------------------------------------------------------------------

        public ImageList Decode()
        {
            if( Data == null || Data.Length <= 12 )
            {
                throw new Exception( "Invalid data" );
            }

            Images = new ImageList();

            ReadHeader();
            ReadBlocks();

            return Images;
        }

        //------------------------------------------------------------------------------

        private Color32[] ReadColourTable( Color32[] colourTable, ImageFlag flags )
        {
            var tableSize = Pow2[ (int)( flags & ImageFlag.TableSizeMask ) + 1 ];

            for( var i = 0; i < tableSize; i++ )
            {
                colourTable[ i ] = new Color32(
                    Data[ D++ ],
                    Data[ D++ ],
                    Data[ D++ ],
                    0xFF
                );
            }

            return colourTable;
        }

        //------------------------------------------------------------------------------

        protected void ReadHeader()
        {
            // signature

            Images.Version = new string( new char[] {
                (char) Data[ 0 ],
                (char) Data[ 1 ],
                (char) Data[ 2 ],
                (char) Data[ 3 ],
                (char) Data[ 4 ],
                (char) Data[ 5 ]
            });

            D = 6;

            if( Images.Version != "GIF87a" && Images.Version != "GIF89a" )
            {
                throw new Exception( "Unsupported GIF version" );
            }

            // read header

            GlobalWidth   = ReadUInt16();
            GlobalHeight  = ReadUInt16();

            Images.Width  = GlobalWidth;
            Images.Height = GlobalHeight;

            var flags = (ImageFlag) ReadByte();

            D++; // background index
            D++; // aspect ratio

            if( flags.HasFlag( ImageFlag.ColourTable ) )
            {
                ReadColourTable( GlobalColourTable, flags );
            }
        }

        //------------------------------------------------------------------------------

        protected void ReadBlocks()
        {
            while( true )
            {
                var block = (Block) ReadByte();

                switch( block )
                {
                    case Block.Image:
                        ReadImageBlock();
                        break;

                    case Block.Extension:

                        var ext = (Extension) ReadByte();

                        switch( ext )
                        {
                            case Extension.GraphicControl:
                                ReadControlBlock();
                                break;

                            default:
                                SkipBlock();
                                break;
                        }

                        break;

                    case Block.End:
                        return;

                    default:
                        throw new Exception( "Unexpected block type" );
                }
            }
        }

        //------------------------------------------------------------------------------

        private void SkipBlock()
        {
            var blockSize = Data[ D++ ];

            while( blockSize != 0x00 )
            {
                D += blockSize;
                blockSize = Data[ D++ ];
            }
        }


        //------------------------------------------------------------------------------

        private void ReadControlBlock()
        {
            D++; // block size

            var flags = Data[ D++ ];

            ControlDispose = (Disposal) ( flags & 0x1C );
            ControlDelay   = ReadUInt16();

            // has transparent colour?

            var transparentColour = Data[ D++ ];

            if( ( flags & 0x01 ) == 0x01 )
            {
                TransparentIndex = transparentColour;
            }
            else
            {
                TransparentIndex = NoTransparency;
            }

            D++; // terminator
        }

        //------------------------------------------------------------------------------

        protected void ReadImageBlock()
        {
            // read image block header

            ImageLeft   = ReadUInt16();
            ImageTop    = ReadUInt16();
            ImageWidth  = ReadUInt16();
            ImageHeight = ReadUInt16();

            var flags   = (ImageFlag) Data[ D++ ];

            if( ImageWidth == 0 || ImageHeight == 0 )
            {
                return;
            }

            if( flags.HasFlag( ImageFlag.ColourTable ) )
            {
                ActiveColourTable = ReadColourTable( LocalColourTable, flags );
            }
            else
            {
                ActiveColourTable = GlobalColourTable;
            }

            // create image

            var img = new Image()
            {
                Width  = GlobalWidth,
                Height = GlobalHeight,
                Delay  = ControlDelay * 10 // (gif are in 1/100th second) convert to ms
            };

            img.RawImage = DecompressLZW(); // minimum code size

            if( flags.HasFlag( ImageFlag.Interlaced ) )
            {
                img.RawImage = Deinterlace( img.RawImage, ImageWidth );
            }

            if( ControlDispose == Disposal.None || ControlDispose == Disposal.DoNotDispose )
            {
                LastImage = img.RawImage;
            }

            Images.Add( img );
        }

        //------------------------------------------------------------------------------

        protected Color32[] Deinterlace( Color32[] input, int width )
        {
            var output   = new Color32[ input.Length ];
            var numRows  = input.Length / width;
            var writePos = input.Length - width; // NB: work backwards due to Y-coord flip

            for( var row = 0; row < numRows; row++ )
            {
                var copyRow = 0;

                // every 8th row starting at 0
                if( row % 8 == 0 )
                {
                    copyRow = row / 8;
                }
                // every 8th row starting at 4
                else if( ( row + 4 ) % 8 == 0 )
                {
                    var o = numRows / 8;
                    copyRow = o + ( row - 4 ) / 8;
                }
                // every 4th row starting at 2
                else if( ( row + 2 ) % 4 == 0 )
                {
                    var o = numRows / 4;
                    copyRow = o + ( row - 2 ) / 4;
                }
                // every 2nd row starting at 1
                else // if( ( r + 1 ) % 2 == 0 )
                {
                    var o = numRows / 2;
                    copyRow = o + ( row - 1 ) / 2;
                }

                Array.Copy( input, ( numRows - copyRow - 1 ) * width, output, writePos, width );

                writePos -= width;
            }

            return output;
        }

        //------------------------------------------------------------------------------
        // DecompressLZW()
        //  LzwCodeSize setup before call
        //  optimised for performance using pre-allocated buffers (cut down on allocation overhead)

        int[]    Pow2      = { 1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1024, 2048, 4096 };

        int[]    codeIndex = new int[ 4098 ];             // codes can be upto 12 bytes long, this is the maximum number of possible codes (2^12 + 2 for clear and end code)
        ushort[] codes     = new ushort[ 128 * 1024 ];    // 128k buffer for codes - should be plenty but we dynamically resize if required

        
        private Color32[] DecompressLZW()
        {
#if mgGIF_UNSAFE
            unsafe
            {
                fixed ( byte* pData = Data )
                {
#endif

            // output write position

            var output    = ControlDispose == Disposal.RestoreBackground || LastImage == null ? new Color32[ GlobalWidth * GlobalHeight ] : LastImage.Clone() as Color32[];
            int row       = ( GlobalHeight - ImageTop - 1 ) * GlobalWidth;
            int col       = ImageLeft;
            int rightEdge = ImageLeft + ImageWidth;

            // setup codes

            int minimumCodeSize = Data[ D++ ];

            if( minimumCodeSize > 11 )
            {
                minimumCodeSize = 11;
            }

            var codeSize        = minimumCodeSize + 1;
            var nextSize        = Pow2[ codeSize ];
            var maximumCodeSize = Pow2[ minimumCodeSize ];
            var clearCode       = maximumCodeSize;
            var endCode         = maximumCodeSize + 1;

            // initialise buffers

            var codesEnd = 0;
            var numCodes = maximumCodeSize + 2;

            for( ushort i = 0; i < numCodes; i++ )
            {
                codeIndex[ i ] = codesEnd;
                codes[ codesEnd++ ] = 1; // length
                codes[ codesEnd++ ] = i; // code
            }

            // LZW decode loop

            uint previousCode   = NoCode; // last code processed
            uint mask           = (uint) ( nextSize - 1 ); // mask out code bits
            uint shiftRegister  = 0; // shift register holds the bytes coming in from the input stream, we shift down by the number of bits

            int  bitsAvailable  = 0; // number of bits available to read in the shift register
            int  bytesAvailable = 0; // number of bytes left in current block

            while( true )
            {
                // get next code

                uint curCode = shiftRegister & mask;

                if( bitsAvailable >= codeSize )
                {
                    bitsAvailable -= codeSize;
                    shiftRegister >>= codeSize;
                }
                else
                {
                    // reload shift register

                    // if start of new block
                    if( bytesAvailable == 0 )
                    {
                        // read blocksize
                        bytesAvailable = Data[ D++ ];

                        // exit if end of stream
                        if( bytesAvailable == 0 )
                        {
                            return output;
                        }
                    }


                    int newBits = 32;

                    if( bytesAvailable >=4 )
                    {
#if mgGIF_UNSAFE
                        shiftRegister = *( (uint*) &pData[D] );
                        D += 4;
#else
                        shiftRegister = (uint) ( Data[ D++ ] | Data[ D++ ] << 8 | Data[ D++ ] << 16 | Data[ D++ ] << 24 );
#endif
                        bytesAvailable -= 4;
                    }
                    else if( bytesAvailable == 3 )
                    {
#if mgGIF_UNSAFE
                        shiftRegister = *( (uint*) &pData[D] );
                        D += 3;
#else
                        shiftRegister = (uint) ( Data[ D++ ] | Data[ D++ ] << 8 | Data[ D++ ] << 16 );
#endif
                        bytesAvailable = 0;
                        newBits        = 24;
                    }
                    else if( bytesAvailable == 2 )
                    {
                        shiftRegister  = (uint) ( Data[ D++ ] | Data[ D++ ] << 8 );
                        bytesAvailable = 0;
                        newBits        = 16;
                    }
                    else
                    {
                        shiftRegister  = Data[ D++ ];
                        bytesAvailable = 0;
                        newBits        = 8;
                    }

                    if( bitsAvailable > 0 )
                    {
                        var bitsRemaining = codeSize - bitsAvailable;
                        curCode |= ( shiftRegister << bitsAvailable ) & mask;
                        shiftRegister >>= bitsRemaining;
                        bitsAvailable = newBits - bitsRemaining;
                    }
                    else
                    {
                        curCode = shiftRegister & mask;
                        shiftRegister >>= codeSize;
                        bitsAvailable = newBits - codeSize;
                    }
                }

                // process code

                bool plusOne = false;
                int  codePos = 0;

                if( curCode == clearCode )
                {
                    // reset codes
                    codeSize = minimumCodeSize + 1;
                    nextSize = Pow2[ codeSize ];
                    numCodes = maximumCodeSize + 2;

                    // reset buffer write pos
                    codesEnd = numCodes * 2;

                    // clear previous code
                    previousCode = NoCode;
                    mask         = (uint) ( nextSize - 1 );

                    continue;
                }
                else if( curCode == endCode )
                {
                    // stop
                    break;
                }
                else if( curCode < numCodes )
                {
                    // write existing code
                    codePos = codeIndex[ curCode ];
                }
                else if( previousCode != NoCode )
                {
                    // write previous code
                    codePos = codeIndex[ previousCode ];
                    plusOne = true;
                }
                else
                {
                    continue;
                }


                // output colours

                var codeLength = codes[ codePos++ ];
                var newCode    = codes[ codePos ];

                for( int i = 0; i < codeLength; i++ )
                {
                    var code = codes[ codePos++ ];

                    if( code != TransparentIndex && col < GlobalWidth )
                    {
                        output[ row + col ] = ActiveColourTable[ code ];
                    }

                    if( ++col == rightEdge )
                    {
                        col = ImageLeft;
                        row -= GlobalWidth;

                        if( row < 0 )
                        {
                            goto Exit;
                        }
                    }
                }

                if( plusOne )
                {
                    if( newCode != TransparentIndex && col < GlobalWidth )
                    {
                        output[ row + col ] = ActiveColourTable[ newCode ];
                    }

                    if( ++col == rightEdge )
                    {
                        col = ImageLeft;
                        row -= GlobalWidth;

                        if( row < 0 )
                        {
                            goto Exit;
                        }
                    }
                }


                // create new code

                if( previousCode != NoCode && numCodes != codeIndex.Length )
                {
                    // get previous code from buffer

                    codePos    = codeIndex[ previousCode ];
                    codeLength = codes[ codePos++ ];

                    // resize buffer if required (should be rare)

                    if( codesEnd + codeLength + 1 >= codes.Length )
                    {
                        Array.Resize( ref codes, codes.Length * 2 );
                    }

                    // add new code

                    codeIndex[ numCodes++ ] = codesEnd;
                    codes[ codesEnd++ ]       = (ushort) ( codeLength + 1 );

                    // copy previous code sequence

                    for( int i=0; i < codeLength; i++ )
                    {
                        codes[ codesEnd++ ] = codes[ codePos++ ];
                    }

                    // append new code

                    codes[ codesEnd++ ] = newCode;
                }

                // increase code size?

                if( numCodes >= nextSize && codeSize < 12 )
                {
                    nextSize = Pow2[ ++codeSize ];
                    mask     = (uint) ( nextSize - 1 );
                }

                // remember last code processed
                previousCode = curCode;
            }

        Exit:

            // skip any remaining bytes
            D += bytesAvailable;

            // consume any remaining blocks
            SkipBlock();

            return output;
        }
#if mgGIF_UNSAFE
    }}
#endif
    }
}
