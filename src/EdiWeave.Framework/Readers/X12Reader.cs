//---------------------------------------------------------------------
// This file is part of ediFabric
//
// Copyright (c) ediFabric. All rights reserved.
//
// THIS CODE AND INFORMATION ARE PROVIDED "AS IS" WITHOUT WARRANTY OF ANY
// KIND, WHETHER EXPRESSED OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
// IMPLIED WARRANTIES OF MERCHANTABILITY AND/OR FITNESS FOR A PARTICULAR
// PURPOSE.
//---------------------------------------------------------------------

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using EdiWeave.Core.Model.Edi;
using EdiWeave.Core.Model.Edi.X12;
using EdiWeave.Framework.Model;

namespace EdiWeave.Framework.Readers
{
    /// <summary>
    /// Reads X12 documents into .NET objects.
    /// </summary>
    public sealed class X12Reader : EdiReader
    {
        private SegmentContext _currentGroupHeader;
        private ISA _currentIsa;
        
        /// <summary>
        /// Initializes a new instance of the <see cref="X12Reader"/> class.
        /// </summary>
        /// <param name="ediStream">The EDI stream to read from.</param>
        /// <param name="rulesAssembly">The name of the assembly containing the EDI classes.</param>
        /// <param name="encoding">The encoding. The default is Encoding.UTF8.</param>
        /// <param name="continueOnError">Whether to continue searching for valid data after an error occurs.</param>
        /// <param name="maxSegmentLength">The maximum length of a segment after which the search for segment terminator seizes.</param>
        /// <returns>A new instance of the <see cref="X12Reader"/> class.</returns>
        public X12Reader(Stream ediStream, string rulesAssembly, Encoding encoding = null, bool continueOnError = false,
            int maxSegmentLength = 5000)
            : base(ediStream, mc => Assembly.Load(new AssemblyName(rulesAssembly)), encoding, continueOnError, maxSegmentLength)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="X12Reader"/> class.
        /// </summary>
        /// <param name="ediStream">The EDI stream to read from.</param>
        /// <param name="rulesAssembly">The delegate to return the assembly containing the EDI classes.</param>
        /// <param name="encoding">The encoding. The default is Encoding.UTF8.</param>
        /// <param name="continueOnError">Whether to continue searching for valid data after an error occurs.</param>
        /// <param name="maxSegmentLength">The maximum length of a segment after which the search for segment terminator seizes.</param>
        /// <returns>A new instance of the <see cref="X12Reader"/> class.</returns>
        public X12Reader(Stream ediStream, Func<MessageContext, Assembly> rulesAssembly, Encoding encoding = null,
            bool continueOnError = false, int maxSegmentLength = 5000)
            : base(ediStream, rulesAssembly, encoding, continueOnError, maxSegmentLength)
        {
        }

        protected override bool TryReadHeader(string segmentName, out string probed, out Separators separators)
        {
            probed = "";
            separators = null;

            try
            {
                if (segmentName == "ISA")
                {
                    var dataElement = Read(1)[0];
                    var isa = Read(102);
                    probed = segmentName + dataElement + isa;

                    if (TryReadFixedWidthIsa(dataElement, isa, probed, out separators))
                        return true;

                    // Some trading partners send an ISA whose fields are not padded to fixed width.
                    // Only accept that at the start of a segment, never for "ISA" found inside data,
                    // and only when every ISA field has the shape the standard requires.
                    int isaLength;
                    if (ProbeAtSegmentStart &&
                        TryReadVariableWidthIsa(dataElement, isa, out isaLength, out separators))
                    {
                        probed = segmentName + dataElement + isa.Substring(0, isaLength);
                        // Return what was read past the ISA terminator, in order, for normal reading.
                        Buffer(isa.Substring(isaLength).TrimEnd('\0'));
                        return true;
                    }

                    separators = null;
                }
            }
            catch (Exception)
            {
                // ignored
            }

            return false;
        }

        private static bool TryReadFixedWidthIsa(char dataElement, string isa, string probed, out Separators separators)
        {
            separators = null;
            var isaElements = isa.Split(dataElement);
            if (isaElements.Length != 16 || isaElements.Any(string.IsNullOrEmpty))
                return false;
            if (isaElements[15].Length != 2)
                return false;
            var componentDataElement = isaElements[15].First();
            char? repetitionDataElement = null;
            if (isaElements[10].First() != 'U')
                repetitionDataElement = isaElements[10].First();
            var segment = isaElements[15].Last();

            var noSegmentTerminator = probed.Substring(0, probed.Length - 1);
            if (noSegmentTerminator.Contains(segment) || isaElements.Any(string.IsNullOrEmpty) ||
                noSegmentTerminator.Contains('\r') || noSegmentTerminator.Contains('\n') ||
                char.IsUpper(segment) || char.IsNumber(segment))
                return false;

            separators = new Separators(segment, componentDataElement, dataElement,
                repetitionDataElement, null);
            return true;
        }

        /// <summary>
        /// Reads an ISA that is not padded to fixed width. <paramref name="isa"/> is the text after
        /// the first data element separator; it may extend past the end of the ISA segment.
        /// </summary>
        /// <param name="isaLength">Length of the ISA within <paramref name="isa"/>, including the terminator.</param>
        private static bool TryReadVariableWidthIsa(char dataElement, string isa, out int isaLength,
            out Separators separators)
        {
            isaLength = 0;
            separators = null;
            if (char.IsLetterOrDigit(dataElement) || char.IsWhiteSpace(dataElement))
                return false;

            // ISA01..ISA15 are separated by 14 separators; the 15th separator precedes ISA16.
            var count = 0;
            var position = -1;
            for (var i = 0; i < isa.Length; i++)
            {
                if (isa[i] == '\r' || isa[i] == '\n')
                    return false;
                if (isa[i] != dataElement)
                    continue;
                if (++count == 15)
                {
                    position = i;
                    break;
                }
            }

            if (position < 0 || position + 2 >= isa.Length)
                return false;

            var component = isa[position + 1];
            var segment = isa[position + 2];
            if (component == dataElement || char.IsLetterOrDigit(component) || char.IsWhiteSpace(component))
                return false;
            if (segment == dataElement || segment == component || char.IsLetterOrDigit(segment) || segment == ' ' ||
                segment == '\0')
                return false;

            var f = isa.Substring(0, position).Split(dataElement);
            if (f.Length != 15)
                return false;

            // ISA01..ISA15 shapes from the X12 standard; a match by chance inside data is very unlikely.
            if (!IsCode(f[0], 2) || f[1].Length > 10 || !IsCode(f[2], 2) || f[3].Length > 10 ||
                !IsCode(f[4], 2) || !HasLength(f[5], 1, 15) || !IsCode(f[6], 2) || !HasLength(f[7], 1, 15) ||
                !IsDigits(f[8], 6, 6) || !IsDigits(f[9], 4, 4) || f[10].Length != 1 ||
                !IsCode(f[11], 5) || !IsDigits(f[12], 1, 9) || !IsDigits(f[13], 1, 1) ||
                f[14].Length != 1 || !char.IsLetter(f[14][0]))
                return false;

            if (f[10][0] == segment || f[10][0] == dataElement || f[10][0] == component)
                return false;
            char? repetition = null;
            if (f[10][0] != 'U')
                repetition = f[10][0];

            isaLength = position + 3;
            separators = new Separators(segment, component, dataElement, repetition, null);
            return true;
        }

        private static bool IsCode(string value, int length)
        {
            return value.Length == length && value.All(char.IsLetterOrDigit);
        }

        private static bool HasLength(string value, int min, int max)
        {
            return value.Length >= min && value.Length <= max;
        }

        private static bool IsDigits(string value, int min, int max)
        {
            return value.Length >= min && value.Length <= max && value.All(char.IsDigit);
        }

        protected override EdiItem Process(string segment)
        {
            EdiItem result = null;

            var segmentContext = new SegmentContext(segment, Separators);
            switch (segmentContext.Name)
            {
                case "ISA":
                case "GS":
                case "ST":
                case "GE":
                case "IEA":
                    result = Flush(segment);
                    if (result != null)
                        return result;
                    break;
            }

            switch (segmentContext.Name)
            {
                case "ISA":
                    var isa = ParseSegment<ISA>(segmentContext.Value, Separators);
                    _currentIsa = isa;
                    result = isa;
                    _currentGroupHeader = null;
                    break;
                case "TA1":
                    result = ParseSegment<TA1>(segmentContext.Value, Separators);
                    _currentGroupHeader = null;
                    break;
                case "GS":
                    result = ParseSegment<GS>(segmentContext.Value, Separators);
                    _currentGroupHeader = segmentContext;
                    break;
                case "ST":
                    CurrentSegments.Add(segmentContext);
                    CurrentMessageContext = BuildContext();
                    SegmentIndex = 0;
                    PartsIndex = 0;
                    break;
                case "SE":
                    CurrentSegments.Add(segmentContext);
                    result = ParseSegments();
                    CurrentMessageContext = null;
                    SegmentIndex = 0;
                    PartsIndex = 0;
                    break;
                case "GE":
                    result = ParseSegment<GE>(segmentContext.Value, Separators);
                    _currentGroupHeader = null;
                    break;
                case "IEA":
                    result = ParseSegment<IEA>(segmentContext.Value, Separators);
                    _currentGroupHeader = null;
                    _currentIsa = null;
                    break;
                default:
                    CurrentSegments.Add(segmentContext);
                    break;
            }

            return result;
        }

        private MessageContext BuildContext()
        {
            if (_currentIsa == null)
                throw new Exception("Interchange header is missing.");

            if (CurrentSegments.Count == 1)
            {
                var ta1 = CurrentSegments.SingleOrDefault(es => es.Name == "TA1");
                if (ta1 != null)
                {
                    return new MessageContext("TA1", "", "", "X12", _currentIsa.InterchangeSenderID_6,
                        _currentIsa.SenderIDQualifier_5, _currentIsa.InterchangeReceiverID_8,
                        _currentIsa.ReceiverIDQualifier_7, RulesAssembly);
                }
            }

            if (_currentGroupHeader == null)
                throw new Exception("GS was not found.");

            var ediCompositeDataElementsGs = _currentGroupHeader.Value.GetDataElements(Separators);
            if (ediCompositeDataElementsGs.Count() < 8)
                throw new Exception("GS is invalid. Too little data elements.");

            var version = ediCompositeDataElementsGs[7];

            var st = CurrentSegments.SingleOrDefault(es => es.Name == "ST");
            if (st == null)
                throw new Exception("ST was not found.");

            var ediCompositeDataElementsSt = st.Value.GetDataElements(Separators);
            // Validate the element count before indexing, so a malformed ST reports a clear error
            // instead of an IndexOutOfRangeException.
            if (ediCompositeDataElementsSt.Length < 2)
                throw new Exception("ST is invalid. Too little data elements.");

            var tag = ediCompositeDataElementsSt[0];
            if (ediCompositeDataElementsSt.Length == 3)
            {
                version = ediCompositeDataElementsSt[2];
            }

            var controlNumber = ediCompositeDataElementsSt[1];

            return new MessageContext(tag, controlNumber, version, "X12", _currentIsa.InterchangeSenderID_6,
                _currentIsa.SenderIDQualifier_5, _currentIsa.InterchangeReceiverID_8, _currentIsa.ReceiverIDQualifier_7, RulesAssembly);
        }
    }
}
