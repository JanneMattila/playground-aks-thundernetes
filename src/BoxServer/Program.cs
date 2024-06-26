﻿using Microsoft.Extensions.Configuration;
using System.Net.Sockets;
using System.Net;
using System.Diagnostics;
using System;

var builder = new ConfigurationBuilder()
    .AddUserSecrets<Program>()
    .AddJsonFile("appsettings.json", optional: true);

var configuration = builder.Build();

var udpPort = configuration.GetValue("udpPort", 3501);

Console.WriteLine($"UDP Port: {udpPort}");

var boxes = new Dictionary<IPEndPoint, Box>();
var listener = new UdpClient(udpPort);
var remoteEndpoint = new IPEndPoint(IPAddress.Any, udpPort);
const int expectedMessageSize = 12;

System.IO.Hashing.Crc32 crc32 = new();
var protocolMagicNumber = new ReadOnlySpan<byte>(BitConverter.GetBytes((short)0xFE));

var stopwatch = new Stopwatch();
stopwatch.Start();

while (true)
{
    byte[] receivedBytes;

    try
    {
        receivedBytes = listener.Receive(ref remoteEndpoint);
    }
    catch (SocketException ex)
    {
        Debug.WriteLine("SocketException caught: {0}", ex.Message);
        continue;
    }

    if (receivedBytes.Length != expectedMessageSize)
    {
        throw new ApplicationException($"Received {receivedBytes.Length} bytes, expected {expectedMessageSize}.");
    }

    crc32.Reset();
    crc32.Append(protocolMagicNumber);
    crc32.Append(new ReadOnlySpan<byte>(receivedBytes, sizeof(short), receivedBytes.Length - sizeof(short)));
    var crc32valueReceived = BitConverter.ToInt32(receivedBytes);
    var crc32valueCalculated = BitConverter.ToInt32(crc32.GetCurrentHash());

    if (crc32valueReceived != crc32valueCalculated)
    {
        throw new ApplicationException($"Received CRC32 value {crc32valueReceived}, calculated {crc32valueCalculated}.");
    }

    var now = DateTime.UtcNow;
    Box box;
    if (!boxes.TryGetValue(remoteEndpoint, out box))
    {
        box = new Box
        {
            ID = (byte)(boxes.Count + 1),
            Address = remoteEndpoint,
            Created = now
        };
        boxes.Add(remoteEndpoint, box);
    }

    box.X = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(receivedBytes, sizeof(short) + crc32.HashLengthInBytes)) / 1_000f;
    box.Y = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(receivedBytes, sizeof(short) + crc32.HashLengthInBytes + sizeof(int))) / 1_000f;
    box.LastUpdated = now;

    // TODO: Add received sequence number to received items list.

    foreach (var b in boxes)
    {
        if (b.Value.ID == box.ID) continue;
        listener.Send(receivedBytes, receivedBytes.Length, b.Key);
    }

    box.Messages++;

    if (stopwatch.ElapsedMilliseconds >= 1_000)
    {
        var lastUpdateThreshold = now.AddSeconds(-5);

        Console.WriteLine($"{boxes.Count} clients:");
        var toRemove = new List<IPEndPoint>();
        foreach (var b in boxes)
        {
            Console.WriteLine($"{b.Key}: {b.Value.Messages} packets, X: {b.Value.X}, Y: {b.Value.Y}");
            if (b.Value.LastUpdated < lastUpdateThreshold)
            {
                toRemove.Add(b.Key);
            }

            b.Value.Messages = 0;
        }

        foreach (var r in toRemove)
        {
            boxes.Remove(r);
        }
        stopwatch.Restart();
    }
}

class Box
{
    public byte ID { get; set; }
    public IPEndPoint Address { get; set; }
    public DateTime Created { get; set; }
    public DateTime LastUpdated { get; set; }
    public int Messages { get; set; }

    public float X { get; set; }
    public float Y { get; set; }
}