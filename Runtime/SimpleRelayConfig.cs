using System;
using UnityEngine;

[CreateAssetMenu(menuName = "Config/SimpleRelayConfig", fileName = "SimpleRelayConfig")]
public class SimpleRelayConfig : ScriptableObject
{
    public string simpleRelayHTTPSURL; // https://abc.def.ghi.com:1234/Prod"
    public string simpleRelayWSSURL; // "wss://abc.def.ghi.com:1234/Prod"
    public DebugLevel srDebugLevel;
    public DebugLevel wsDebugLevel;

    [Serializable]
    public enum DebugLevel
    {
        Error,
        Warning,
        Info
    }
}
