﻿using System;
using System.Collections.Generic;
using System.IO;

namespace Rebus.Messages
{
    /// <summary>
    /// Transport message wrapper that has a set of headers and a stream of raw data to be sent/received
    /// </summary>
    public class TransportMessage
    {
        public TransportMessage(Dictionary<string, string> headers, Stream body)
        {
            if (headers == null) throw new ArgumentNullException("headers");
            if (body == null) throw new ArgumentNullException("body");
            Headers = headers;
            Body = body;
        }

        public Dictionary<string, string> Headers { get; private set; }
        
        public Stream Body { get; private set; }
    }
}