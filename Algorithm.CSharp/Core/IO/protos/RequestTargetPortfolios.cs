// <auto-generated>
//     Generated by the protocol buffer compiler.  DO NOT EDIT!
//     source: RequestTargetPortfolios.proto
// </auto-generated>
#pragma warning disable 1591, 0612, 3021, 8981
#region Designer generated code

using pb = global::Google.Protobuf;
using pbc = global::Google.Protobuf.Collections;
using pbr = global::Google.Protobuf.Reflection;
using scg = global::System.Collections.Generic;
namespace QuantConnect.Algorithm.CSharp.Core.IO {

  /// <summary>Holder for reflection information generated from RequestTargetPortfolios.proto</summary>
  public static partial class RequestTargetPortfoliosReflection {

    #region Descriptor
    /// <summary>File descriptor for RequestTargetPortfolios.proto</summary>
    public static pbr::FileDescriptor Descriptor {
      get { return descriptor; }
    }
    private static pbr::FileDescriptor descriptor;

    static RequestTargetPortfoliosReflection() {
      byte[] descriptorData = global::System.Convert.FromBase64String(
          string.Concat(
            "Ch1SZXF1ZXN0VGFyZ2V0UG9ydGZvbGlvcy5wcm90bxIlUXVhbnRDb25uZWN0",
            "LkFsZ29yaXRobS5DU2hhcnAuQ29yZS5JTyK2AwoXUmVxdWVzdFRhcmdldFBv",
            "cnRmb2xpb3MSCgoCdHMYASABKAkSEgoKdW5kZXJseWluZxgCIAEoCRIYChB1",
            "bmRlcmx5aW5nX3ByaWNlGAMgASgCEl4KCGhvbGRpbmdzGAQgAygLMkwuUXVh",
            "bnRDb25uZWN0LkFsZ29yaXRobS5DU2hhcnAuQ29yZS5JTy5SZXF1ZXN0VGFy",
            "Z2V0UG9ydGZvbGlvcy5Ib2xkaW5nc0VudHJ5EmcKDW9wdGlvbl9xdW90ZXMY",
            "BSADKAsyUC5RdWFudENvbm5lY3QuQWxnb3JpdGhtLkNTaGFycC5Db3JlLklP",
            "LlJlcXVlc3RUYXJnZXRQb3J0Zm9saW9zLk9wdGlvblF1b3Rlc0VudHJ5Gi8K",
            "DUhvbGRpbmdzRW50cnkSCwoDa2V5GAEgASgJEg0KBXZhbHVlGAIgASgCOgI4",
            "ARpnChFPcHRpb25RdW90ZXNFbnRyeRILCgNrZXkYASABKAkSQQoFdmFsdWUY",
            "AiABKAsyMi5RdWFudENvbm5lY3QuQWxnb3JpdGhtLkNTaGFycC5Db3JlLklP",
            "Lk9wdGlvblF1b3RlOgI4ASInCgtPcHRpb25RdW90ZRILCgNiaWQYASABKAIS",
            "CwoDYXNrGAIgASgCQiiqAiVRdWFudENvbm5lY3QuQWxnb3JpdGhtLkNTaGFy",
            "cC5Db3JlLklPYgZwcm90bzM="));
      descriptor = pbr::FileDescriptor.FromGeneratedCode(descriptorData,
          new pbr::FileDescriptor[] { },
          new pbr::GeneratedClrTypeInfo(null, null, new pbr::GeneratedClrTypeInfo[] {
            new pbr::GeneratedClrTypeInfo(typeof(global::QuantConnect.Algorithm.CSharp.Core.IO.RequestTargetPortfolios), global::QuantConnect.Algorithm.CSharp.Core.IO.RequestTargetPortfolios.Parser, new[]{ "Ts", "Underlying", "UnderlyingPrice", "Holdings", "OptionQuotes" }, null, null, null, new pbr::GeneratedClrTypeInfo[] { null, null, }),
            new pbr::GeneratedClrTypeInfo(typeof(global::QuantConnect.Algorithm.CSharp.Core.IO.OptionQuote), global::QuantConnect.Algorithm.CSharp.Core.IO.OptionQuote.Parser, new[]{ "Bid", "Ask" }, null, null, null, null)
          }));
    }
    #endregion

  }
  #region Messages
  [global::System.Diagnostics.DebuggerDisplayAttribute("{ToString(),nq}")]
  public sealed partial class RequestTargetPortfolios : pb::IMessage<RequestTargetPortfolios>
  #if !GOOGLE_PROTOBUF_REFSTRUCT_COMPATIBILITY_MODE
      , pb::IBufferMessage
  #endif
  {
    private static readonly pb::MessageParser<RequestTargetPortfolios> _parser = new pb::MessageParser<RequestTargetPortfolios>(() => new RequestTargetPortfolios());
    private pb::UnknownFieldSet _unknownFields;
    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public static pb::MessageParser<RequestTargetPortfolios> Parser { get { return _parser; } }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public static pbr::MessageDescriptor Descriptor {
      get { return global::QuantConnect.Algorithm.CSharp.Core.IO.RequestTargetPortfoliosReflection.Descriptor.MessageTypes[0]; }
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    pbr::MessageDescriptor pb::IMessage.Descriptor {
      get { return Descriptor; }
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public RequestTargetPortfolios() {
      OnConstruction();
    }

    partial void OnConstruction();

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public RequestTargetPortfolios(RequestTargetPortfolios other) : this() {
      ts_ = other.ts_;
      underlying_ = other.underlying_;
      underlyingPrice_ = other.underlyingPrice_;
      holdings_ = other.holdings_.Clone();
      optionQuotes_ = other.optionQuotes_.Clone();
      _unknownFields = pb::UnknownFieldSet.Clone(other._unknownFields);
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public RequestTargetPortfolios Clone() {
      return new RequestTargetPortfolios(this);
    }

    /// <summary>Field number for the "ts" field.</summary>
    public const int TsFieldNumber = 1;
    private string ts_ = "";
    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public string Ts {
      get { return ts_; }
      set {
        ts_ = pb::ProtoPreconditions.CheckNotNull(value, "value");
      }
    }

    /// <summary>Field number for the "underlying" field.</summary>
    public const int UnderlyingFieldNumber = 2;
    private string underlying_ = "";
    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public string Underlying {
      get { return underlying_; }
      set {
        underlying_ = pb::ProtoPreconditions.CheckNotNull(value, "value");
      }
    }

    /// <summary>Field number for the "underlying_price" field.</summary>
    public const int UnderlyingPriceFieldNumber = 3;
    private float underlyingPrice_;
    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public float UnderlyingPrice {
      get { return underlyingPrice_; }
      set {
        underlyingPrice_ = value;
      }
    }

    /// <summary>Field number for the "holdings" field.</summary>
    public const int HoldingsFieldNumber = 4;
    private static readonly pbc::MapField<string, float>.Codec _map_holdings_codec
        = new pbc::MapField<string, float>.Codec(pb::FieldCodec.ForString(10, ""), pb::FieldCodec.ForFloat(21, 0F), 34);
    private readonly pbc::MapField<string, float> holdings_ = new pbc::MapField<string, float>();
    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public pbc::MapField<string, float> Holdings {
      get { return holdings_; }
    }

    /// <summary>Field number for the "option_quotes" field.</summary>
    public const int OptionQuotesFieldNumber = 5;
    private static readonly pbc::MapField<string, global::QuantConnect.Algorithm.CSharp.Core.IO.OptionQuote>.Codec _map_optionQuotes_codec
        = new pbc::MapField<string, global::QuantConnect.Algorithm.CSharp.Core.IO.OptionQuote>.Codec(pb::FieldCodec.ForString(10, ""), pb::FieldCodec.ForMessage(18, global::QuantConnect.Algorithm.CSharp.Core.IO.OptionQuote.Parser), 42);
    private readonly pbc::MapField<string, global::QuantConnect.Algorithm.CSharp.Core.IO.OptionQuote> optionQuotes_ = new pbc::MapField<string, global::QuantConnect.Algorithm.CSharp.Core.IO.OptionQuote>();
    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public pbc::MapField<string, global::QuantConnect.Algorithm.CSharp.Core.IO.OptionQuote> OptionQuotes {
      get { return optionQuotes_; }
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public override bool Equals(object other) {
      return Equals(other as RequestTargetPortfolios);
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public bool Equals(RequestTargetPortfolios other) {
      if (ReferenceEquals(other, null)) {
        return false;
      }
      if (ReferenceEquals(other, this)) {
        return true;
      }
      if (Ts != other.Ts) return false;
      if (Underlying != other.Underlying) return false;
      if (!pbc::ProtobufEqualityComparers.BitwiseSingleEqualityComparer.Equals(UnderlyingPrice, other.UnderlyingPrice)) return false;
      if (!Holdings.Equals(other.Holdings)) return false;
      if (!OptionQuotes.Equals(other.OptionQuotes)) return false;
      return Equals(_unknownFields, other._unknownFields);
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public override int GetHashCode() {
      int hash = 1;
      if (Ts.Length != 0) hash ^= Ts.GetHashCode();
      if (Underlying.Length != 0) hash ^= Underlying.GetHashCode();
      if (UnderlyingPrice != 0F) hash ^= pbc::ProtobufEqualityComparers.BitwiseSingleEqualityComparer.GetHashCode(UnderlyingPrice);
      hash ^= Holdings.GetHashCode();
      hash ^= OptionQuotes.GetHashCode();
      if (_unknownFields != null) {
        hash ^= _unknownFields.GetHashCode();
      }
      return hash;
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public override string ToString() {
      return pb::JsonFormatter.ToDiagnosticString(this);
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public void WriteTo(pb::CodedOutputStream output) {
    #if !GOOGLE_PROTOBUF_REFSTRUCT_COMPATIBILITY_MODE
      output.WriteRawMessage(this);
    #else
      if (Ts.Length != 0) {
        output.WriteRawTag(10);
        output.WriteString(Ts);
      }
      if (Underlying.Length != 0) {
        output.WriteRawTag(18);
        output.WriteString(Underlying);
      }
      if (UnderlyingPrice != 0F) {
        output.WriteRawTag(29);
        output.WriteFloat(UnderlyingPrice);
      }
      holdings_.WriteTo(output, _map_holdings_codec);
      optionQuotes_.WriteTo(output, _map_optionQuotes_codec);
      if (_unknownFields != null) {
        _unknownFields.WriteTo(output);
      }
    #endif
    }

    #if !GOOGLE_PROTOBUF_REFSTRUCT_COMPATIBILITY_MODE
    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    void pb::IBufferMessage.InternalWriteTo(ref pb::WriteContext output) {
      if (Ts.Length != 0) {
        output.WriteRawTag(10);
        output.WriteString(Ts);
      }
      if (Underlying.Length != 0) {
        output.WriteRawTag(18);
        output.WriteString(Underlying);
      }
      if (UnderlyingPrice != 0F) {
        output.WriteRawTag(29);
        output.WriteFloat(UnderlyingPrice);
      }
      holdings_.WriteTo(ref output, _map_holdings_codec);
      optionQuotes_.WriteTo(ref output, _map_optionQuotes_codec);
      if (_unknownFields != null) {
        _unknownFields.WriteTo(ref output);
      }
    }
    #endif

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public int CalculateSize() {
      int size = 0;
      if (Ts.Length != 0) {
        size += 1 + pb::CodedOutputStream.ComputeStringSize(Ts);
      }
      if (Underlying.Length != 0) {
        size += 1 + pb::CodedOutputStream.ComputeStringSize(Underlying);
      }
      if (UnderlyingPrice != 0F) {
        size += 1 + 4;
      }
      size += holdings_.CalculateSize(_map_holdings_codec);
      size += optionQuotes_.CalculateSize(_map_optionQuotes_codec);
      if (_unknownFields != null) {
        size += _unknownFields.CalculateSize();
      }
      return size;
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public void MergeFrom(RequestTargetPortfolios other) {
      if (other == null) {
        return;
      }
      if (other.Ts.Length != 0) {
        Ts = other.Ts;
      }
      if (other.Underlying.Length != 0) {
        Underlying = other.Underlying;
      }
      if (other.UnderlyingPrice != 0F) {
        UnderlyingPrice = other.UnderlyingPrice;
      }
      holdings_.MergeFrom(other.holdings_);
      optionQuotes_.MergeFrom(other.optionQuotes_);
      _unknownFields = pb::UnknownFieldSet.MergeFrom(_unknownFields, other._unknownFields);
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public void MergeFrom(pb::CodedInputStream input) {
    #if !GOOGLE_PROTOBUF_REFSTRUCT_COMPATIBILITY_MODE
      input.ReadRawMessage(this);
    #else
      uint tag;
      while ((tag = input.ReadTag()) != 0) {
        switch(tag) {
          default:
            _unknownFields = pb::UnknownFieldSet.MergeFieldFrom(_unknownFields, input);
            break;
          case 10: {
            Ts = input.ReadString();
            break;
          }
          case 18: {
            Underlying = input.ReadString();
            break;
          }
          case 29: {
            UnderlyingPrice = input.ReadFloat();
            break;
          }
          case 34: {
            holdings_.AddEntriesFrom(input, _map_holdings_codec);
            break;
          }
          case 42: {
            optionQuotes_.AddEntriesFrom(input, _map_optionQuotes_codec);
            break;
          }
        }
      }
    #endif
    }

    #if !GOOGLE_PROTOBUF_REFSTRUCT_COMPATIBILITY_MODE
    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    void pb::IBufferMessage.InternalMergeFrom(ref pb::ParseContext input) {
      uint tag;
      while ((tag = input.ReadTag()) != 0) {
        switch(tag) {
          default:
            _unknownFields = pb::UnknownFieldSet.MergeFieldFrom(_unknownFields, ref input);
            break;
          case 10: {
            Ts = input.ReadString();
            break;
          }
          case 18: {
            Underlying = input.ReadString();
            break;
          }
          case 29: {
            UnderlyingPrice = input.ReadFloat();
            break;
          }
          case 34: {
            holdings_.AddEntriesFrom(ref input, _map_holdings_codec);
            break;
          }
          case 42: {
            optionQuotes_.AddEntriesFrom(ref input, _map_optionQuotes_codec);
            break;
          }
        }
      }
    }
    #endif

  }

  [global::System.Diagnostics.DebuggerDisplayAttribute("{ToString(),nq}")]
  public sealed partial class OptionQuote : pb::IMessage<OptionQuote>
  #if !GOOGLE_PROTOBUF_REFSTRUCT_COMPATIBILITY_MODE
      , pb::IBufferMessage
  #endif
  {
    private static readonly pb::MessageParser<OptionQuote> _parser = new pb::MessageParser<OptionQuote>(() => new OptionQuote());
    private pb::UnknownFieldSet _unknownFields;
    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public static pb::MessageParser<OptionQuote> Parser { get { return _parser; } }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public static pbr::MessageDescriptor Descriptor {
      get { return global::QuantConnect.Algorithm.CSharp.Core.IO.RequestTargetPortfoliosReflection.Descriptor.MessageTypes[1]; }
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    pbr::MessageDescriptor pb::IMessage.Descriptor {
      get { return Descriptor; }
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public OptionQuote() {
      OnConstruction();
    }

    partial void OnConstruction();

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public OptionQuote(OptionQuote other) : this() {
      bid_ = other.bid_;
      ask_ = other.ask_;
      _unknownFields = pb::UnknownFieldSet.Clone(other._unknownFields);
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public OptionQuote Clone() {
      return new OptionQuote(this);
    }

    /// <summary>Field number for the "bid" field.</summary>
    public const int BidFieldNumber = 1;
    private float bid_;
    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public float Bid {
      get { return bid_; }
      set {
        bid_ = value;
      }
    }

    /// <summary>Field number for the "ask" field.</summary>
    public const int AskFieldNumber = 2;
    private float ask_;
    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public float Ask {
      get { return ask_; }
      set {
        ask_ = value;
      }
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public override bool Equals(object other) {
      return Equals(other as OptionQuote);
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public bool Equals(OptionQuote other) {
      if (ReferenceEquals(other, null)) {
        return false;
      }
      if (ReferenceEquals(other, this)) {
        return true;
      }
      if (!pbc::ProtobufEqualityComparers.BitwiseSingleEqualityComparer.Equals(Bid, other.Bid)) return false;
      if (!pbc::ProtobufEqualityComparers.BitwiseSingleEqualityComparer.Equals(Ask, other.Ask)) return false;
      return Equals(_unknownFields, other._unknownFields);
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public override int GetHashCode() {
      int hash = 1;
      if (Bid != 0F) hash ^= pbc::ProtobufEqualityComparers.BitwiseSingleEqualityComparer.GetHashCode(Bid);
      if (Ask != 0F) hash ^= pbc::ProtobufEqualityComparers.BitwiseSingleEqualityComparer.GetHashCode(Ask);
      if (_unknownFields != null) {
        hash ^= _unknownFields.GetHashCode();
      }
      return hash;
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public override string ToString() {
      return pb::JsonFormatter.ToDiagnosticString(this);
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public void WriteTo(pb::CodedOutputStream output) {
    #if !GOOGLE_PROTOBUF_REFSTRUCT_COMPATIBILITY_MODE
      output.WriteRawMessage(this);
    #else
      if (Bid != 0F) {
        output.WriteRawTag(13);
        output.WriteFloat(Bid);
      }
      if (Ask != 0F) {
        output.WriteRawTag(21);
        output.WriteFloat(Ask);
      }
      if (_unknownFields != null) {
        _unknownFields.WriteTo(output);
      }
    #endif
    }

    #if !GOOGLE_PROTOBUF_REFSTRUCT_COMPATIBILITY_MODE
    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    void pb::IBufferMessage.InternalWriteTo(ref pb::WriteContext output) {
      if (Bid != 0F) {
        output.WriteRawTag(13);
        output.WriteFloat(Bid);
      }
      if (Ask != 0F) {
        output.WriteRawTag(21);
        output.WriteFloat(Ask);
      }
      if (_unknownFields != null) {
        _unknownFields.WriteTo(ref output);
      }
    }
    #endif

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public int CalculateSize() {
      int size = 0;
      if (Bid != 0F) {
        size += 1 + 4;
      }
      if (Ask != 0F) {
        size += 1 + 4;
      }
      if (_unknownFields != null) {
        size += _unknownFields.CalculateSize();
      }
      return size;
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public void MergeFrom(OptionQuote other) {
      if (other == null) {
        return;
      }
      if (other.Bid != 0F) {
        Bid = other.Bid;
      }
      if (other.Ask != 0F) {
        Ask = other.Ask;
      }
      _unknownFields = pb::UnknownFieldSet.MergeFrom(_unknownFields, other._unknownFields);
    }

    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    public void MergeFrom(pb::CodedInputStream input) {
    #if !GOOGLE_PROTOBUF_REFSTRUCT_COMPATIBILITY_MODE
      input.ReadRawMessage(this);
    #else
      uint tag;
      while ((tag = input.ReadTag()) != 0) {
        switch(tag) {
          default:
            _unknownFields = pb::UnknownFieldSet.MergeFieldFrom(_unknownFields, input);
            break;
          case 13: {
            Bid = input.ReadFloat();
            break;
          }
          case 21: {
            Ask = input.ReadFloat();
            break;
          }
        }
      }
    #endif
    }

    #if !GOOGLE_PROTOBUF_REFSTRUCT_COMPATIBILITY_MODE
    [global::System.Diagnostics.DebuggerNonUserCodeAttribute]
    [global::System.CodeDom.Compiler.GeneratedCode("protoc", null)]
    void pb::IBufferMessage.InternalMergeFrom(ref pb::ParseContext input) {
      uint tag;
      while ((tag = input.ReadTag()) != 0) {
        switch(tag) {
          default:
            _unknownFields = pb::UnknownFieldSet.MergeFieldFrom(_unknownFields, ref input);
            break;
          case 13: {
            Bid = input.ReadFloat();
            break;
          }
          case 21: {
            Ask = input.ReadFloat();
            break;
          }
        }
      }
    }
    #endif

  }

  #endregion

}

#endregion Designer generated code
