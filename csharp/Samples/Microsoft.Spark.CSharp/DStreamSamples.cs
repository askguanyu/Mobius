﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Spark.CSharp.Core;

using Microsoft.Spark.CSharp.Streaming;
using Microsoft.Spark.CSharp.Samples;

using NUnit.Framework;

namespace Microsoft.Spark.CSharp
{
    class DStreamSamples
    {
        private static int count;
        private static bool stopFileServer;
        private static void StartFileServer(string directory, string pattern, int loop)
        {
            string testDir = Path.Combine(directory, "test");
            if (!Directory.Exists(testDir))
                Directory.CreateDirectory(testDir);

            stopFileServer = false;

            string[] files = Directory.GetFiles(directory, pattern);

            Task.Run(() =>
            {
                while (!stopFileServer)
                {
                    DateTime now = DateTime.Now;
                    foreach (string path in files)
                    {
                        string text = File.ReadAllText(path);
                        File.WriteAllText(testDir + "\\" + now.ToBinary() + "_" + Path.GetFileName(path), text);
                    }
                    System.Threading.Thread.Sleep(200);
                }

                System.Threading.Thread.Sleep(3000);

                foreach (var file in Directory.GetFiles(testDir, "*"))
                    File.Delete(file);
            });
        }

        [Sample("experimental")]
        internal static void DStreamTextFileSamples()
        {
            string directory = SparkCLRSamples.Configuration.SampleDataLocation;
            string checkpointPath = Path.Combine(directory, "checkpoint");

            StreamingContext ssc = StreamingContext.GetOrCreate(checkpointPath,
                () =>
                {
                    SparkContext sc = SparkCLRSamples.SparkContext;
                    StreamingContext context = new StreamingContext(sc, 2000);
                    context.Checkpoint(checkpointPath);

                    var lines = context.TextFileStream(Path.Combine(directory, "test"));
                    lines = context.Union(lines, lines);
                    var words = lines.FlatMap(l => l.Split(' '));
                    var pairs = words.Map(w => new KeyValuePair<string, int>(w, 1));

                    // since operations like ReduceByKey, Join and UpdateStateByKey are
                    // separate dstream transformations defined in CSharpDStream.scala
                    // an extra CSharpRDD is introduced in between these operations
                    var wordCounts = pairs.ReduceByKey((x, y) => x + y);
                    var join = wordCounts.Join(wordCounts, 2);
                    var state = join.UpdateStateByKey<string, Tuple<int, int>, int>((vs, s) => vs.Sum(x => x.Item1 + x.Item2) + s);

                    state.ForeachRDD((time, rdd) =>
                    {
                        // there's chance rdd.Take conflicts with ssc.Stop
                        if (stopFileServer)
                            return;

                        object[] taken = rdd.Take(10);
                        Console.WriteLine("-------------------------------------------");
                        Console.WriteLine("Time: {0}", time);
                        Console.WriteLine("-------------------------------------------");
                        foreach (object record in taken)
                        {
                            Console.WriteLine(record);
                        }
                        Console.WriteLine();

                        stopFileServer = count++ > 100;
                    });

                    return context;
                });

            ssc.Start();

            StartFileServer(directory, "words.txt", 100);

            ssc.AwaitTermination();
            ssc.Stop();
        }

        /// <summary>
        /// start a local kafka service and create a 'test' topic with some data before running this sample
        /// e.g. create 2 partition with 100 messages which will be repartitioned into 10 RDD partitions
        /// </summary>
        [Sample("experimental")]
        internal static void DStreamDirectKafkaWithRepartitionSample()
        {
            string directory = SparkCLRSamples.Configuration.SampleDataLocation;
            string checkpointPath = Path.Combine(directory, "checkpoint");

            StreamingContext ssc = StreamingContext.GetOrCreate(checkpointPath,
                () =>
                {
                    SparkContext sc = SparkCLRSamples.SparkContext;
                    StreamingContext context = new StreamingContext(sc, 2000);
                    context.Checkpoint(checkpointPath);

                    var kafkaParams = new Dictionary<string, string> {
                        {"metadata.broker.list", "127.0.0.1:9092"},
                        {"auto.offset.reset", "smallest"}
                    };

                    var dstream = KafkaUtils.CreateDirectStreamWithRepartition(context, new List<string> { "test" }, kafkaParams, new Dictionary<string, long>(), 10);

                    dstream.ForeachRDD((time, rdd) => 
                        {
                            long batchCount = rdd.Count();
                            int partitions = rdd.GetNumPartitions();

                            Console.WriteLine("-------------------------------------------");
                            Console.WriteLine("Time: {0}", time);
                            Console.WriteLine("-------------------------------------------");
                            Console.WriteLine("Count: " + batchCount);
                            Console.WriteLine("Partitions: " + partitions);
                            
                            // only first batch has data and is repartitioned into 10 partitions
                            if (count++ == 0)
                            {
                                Assert.AreEqual(100, batchCount);
                                Assert.IsTrue(partitions >= 10);
                            }
                            else
                            {
                                Assert.AreEqual(0, batchCount);
                                Assert.IsTrue(partitions == 0);
                            }
                        });

                    return context;
                });

            ssc.Start();
            ssc.AwaitTermination();
        }
    }
}
