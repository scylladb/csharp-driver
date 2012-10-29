﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Numerics;
using Cassandra.Native;
using System.IO;

namespace MyUTExt
{
    public class Randomm : Random
    {
        public Randomm()
            : base(5)
        {
        }

        public float NextSingle()
        {
            double numb = this.NextDouble();
            numb -= 0.5;
            numb *= 2;
            return float.MaxValue * (float)numb;
        }
        public UInt16 NextUInt16()
        {
            return (ushort)this.Next(0, 65535); 
        }
        public int NextInt32()
        {
            return this.Next();
        }
        public Int64 NextInt64()
        {
            var buffer = new byte[sizeof(Int64)];
            this.NextBytes(buffer);
            return BitConverter.ToInt64(buffer, 0);
        }

        public decimal NextDecimal()
        {
            byte scale = (byte)this.Next(29);
            bool sign = this.Next(2) == 1;
            
            return new decimal(this.NextInt32(),
                               this.NextInt32(),
                               this.NextInt32(),
                               sign,
                               scale);
        }
        //public DecimalBuffer NextDecimal()
        //{
        //    //byte scale = (byte)this.Next(29);
        //    //bool sign = this.Next(2) == 1;
        //    //decimal number = new decimal(this.NextInt32(),
        //    //       this.NextInt32(),
        //    //       this.NextInt32(),
        //    //       sign,
        //    //       scale);            
            
        //    //byte[] bArray = null;

        //    //MemoryStream memStream = new MemoryStream();
        //    //BinaryWriter writer = new BinaryWriter(memStream);
        //    //writer.Write(number);
        //    //bArray = memStream.ToArray();
        //    //memStream.Close();
        //    //writer.Close();
            
        //    //DecimalBuffer decbuf;
                
        //    //decbuf.BigIntegerBytes = bArray;
        //    //decbuf.Scale = scale;

        //    return Extensions.ToDecimalBuffer(NextDecimalNormal()); ;
        //}

        public BigInteger NextBigInteger()
        {
            return new BigInteger(Int64.MaxValue) * 10;
        }

        //public VarintBuffer NextBigInteger()
        //{
        //    return Extensions.ToVarintBuffer(NextBigIntegerNormal());
        //}

        public string NextString()
        {
            return NextChar();
        }
        public string NextChar()
        {            
            string asciiString = String.Empty;
            for (int i = 0; i < 128; i++)
                if (i == 34 || i == 39)
                    continue;
                else 
                    asciiString += (char)i;

            return asciiString;
        }
        public DateTimeOffset NextDateTimeOffset()
        {
            return DateTimeOffset.Now.DateTime;
        }

        public byte[] NextByte()
        {
            byte[] btarr = new byte[this.NextUInt16()];            
            this.NextBytes(btarr);
            return btarr;
        }


    }
}
