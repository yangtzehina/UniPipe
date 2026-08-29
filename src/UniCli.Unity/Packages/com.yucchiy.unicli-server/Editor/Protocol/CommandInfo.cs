using System;

namespace UniCli.Protocol
{
    [Serializable]
    public class CommandInfo
    {
        public string name;
        public string description;
        public bool builtIn;
        public string module;

        // Declared traits (see CommandPreconditionAttribute on the server). Null/false when the
        // command declares nothing, so older servers and cached listings stay valid.
        public string requiresEditorState;
        public bool replacesOpenScenes;
        public bool destructive;

        public CommandFieldInfo[] requestFields;
        public CommandFieldInfo[] responseFields;
        public CommandTypeDetail[] requestTypeDetails;
        public CommandTypeDetail[] responseTypeDetails;
    }

    [Serializable]
    public class CommandFieldInfo
    {
        public string name;
        public string type;
        public string typeId;
        public string defaultValue;
    }

    [Serializable]
    public class CommandTypeDetail
    {
        public string typeName;
        public string typeId;
        public CommandFieldInfo[] fields;
    }

    [Serializable]
    public class CommandListResponse
    {
        public CommandInfo[] commands;
    }
}
