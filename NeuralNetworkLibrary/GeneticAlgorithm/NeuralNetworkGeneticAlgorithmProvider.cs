﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NeuralNetworkLibrary.GeneticAlgorithm.Misc;
using NeuralNetworkLibrary.Helpers;
using NeuralNetworkLibrary.Networks;
using NeuralNetworkLibrary.Networks.Implementations;
using NeuralNetworkLibrary.Networks.PublicAPIs;

namespace NeuralNetworkLibrary.GeneticAlgorithm
{
    /// <summary>
    /// A provider that uses a genetic algorithm to breed different species of neural networks to maximize a given fitness function
    /// </summary>
    public class NeuralNetworkGeneticAlgorithmProvider
    {
        #region Fields and parameters

        /// <summary>
        /// Gets the size of the input layer
        /// </summary>
        public int InputLayerSize { get; }

        /// <summary>
        /// Gets the size of the output layer
        /// </summary>
        public int OutputLayerSize { get; }

        /// <summary>
        /// Gets the size of the population for the genetic algorithm
        /// </summary>
        public int PopulationSize { get; }

        /// <summary>
        /// Gets the mutation probability for each weight in the neural networks
        /// </summary>
        public int WeightsMutationRate { get; }

        /// <summary>
        /// Gets the number of best networks to copy over to each new generation
        /// </summary>
        public int EliteSamples { get; }

        /// <summary>
        /// Gets the number of neurons in the first hidden layer
        /// </summary>
        public int FirstHiddenLayerSize { get; }

        /// <summary>
        /// Gets the number of neurons in the second hidden layer
        /// </summary>
        public int SecondHiddenLayerSize { get; }

        // Threshold for the first hidden layer
        private readonly double? Z1Threshold;

        // Threshold for the second layer
        private readonly double? Z2Threshold;

        // Threshold for the third layer (optional)
        private readonly double? Z3Threshold;

        #endregion

        #region Genetic algorithm public parameters

        /// <summary>
        /// Represents the forward method used to process the input data using a neural network
        /// </summary>
        /// <param name="input">The input data to process</param>
        public delegate double[,] ForwardFunction(double[,] input);

        /// <summary>
        /// Represents the method used to calculate the fitness score for each neural network
        /// </summary>
        /// <param name="uid">A unique identifier for the network</param>
        /// <param name="forwardFunction">The forward function to test the current neural network</param>
        public delegate double FitnessDelegate(int uid, ForwardFunction forwardFunction);

        /// <summary>
        /// Gets the function used to evaluate the fitness of every generated network
        /// </summary>
        public FitnessDelegate FitnessFunction { get; }

        /// <summary>
        /// Gets or sets the callback action used to report the progress
        /// </summary>
        public Action<GeneticAlgorithmProgress> ProgressCallback { get; set; }

        /// <summary>
        /// Gets the number of the current generation since the provider instance was created
        /// </summary>
        public int Generation { get; private set; }

        /// <summary>
        /// Gets whether or not the provider instance is currently running the genetic algorithm
        /// </summary>
        public bool IsRunning => _Cts != null;

        #endregion

        #region Working set fields

        /// <summary>
        /// Gets the random instance used in the genetic algorithm
        /// </summary>
        private readonly Random RandomProvider = new Random();

        /// <summary>
        /// Gets the current population for the genetic algorithm
        /// </summary>
        private NeuralNetworkBase[] _Population;


        /// <summary>
        /// Gets the semaphore used to synchronize the genetic algorithm execution
        /// </summary>
        private readonly SemaphoreSlim RunningSemaphore = new SemaphoreSlim(1);

        /// <summary>
        /// Gets the cacellation token to stop the genetic algorithm
        /// </summary>
        private CancellationTokenSource _Cts;

        #endregion

        #region Best network

        private Tuple<NeuralNetworkBase, double> _BestResult;

        /// <summary>
        /// Gets or sets the current best result produced by the genetic algorithm
        /// </summary>
        private Tuple<NeuralNetworkBase, double> BestResult
        {
            get { return _BestResult; }
            set
            {
                if (_BestResult != value)
                {
                    _BestResult = value;
                    BestNetworkChanged?.Invoke(this, new GeneticAlgorithmBestNetworkChangedEventArgs(value.Item1, value.Item2));
                }
            }
        }

        /// <summary>
        /// Gets the maximum fitness score reached by the provider instance
        /// </summary>
        public double BestFitness => _BestResult?.Item2 ?? double.MinValue;

        /// <summary>
        /// Gets the current best network generated by the provider
        /// </summary>
        public INeuralNetwork BestNetwork => BestResult?.Item1;

        /// <summary>
        /// Raised whenever the genetic algorithm produces a better neural network for the current fitness function
        /// </summary>
        public event EventHandler<GeneticAlgorithmBestNetworkChangedEventArgs> BestNetworkChanged;

        #endregion

        #region Initialization

        // Private constructor
        private NeuralNetworkGeneticAlgorithmProvider(
            FitnessDelegate fitnessFunction,
            int input, int output, int firstHiddenSize, int secondHiddenSize, double? z1Th, double? z2Th, double? z3Th,
            int population, int weightsMutationRate, int eliteSamples)
        {
            // Input checks
            if (fitnessFunction == null) throw new ArgumentNullException("The fitness function can't be null");
            if (input <= 0 || output <= 0 || firstHiddenSize <= 0)
            {
                throw new ArgumentOutOfRangeException("The input layer, the output layer and the first hidden layer must have at least one neuron each");
            }
            if (secondHiddenSize < 0) throw new ArgumentOutOfRangeException("The size of the second hidden layer can't be negative");
            if ((z1Th.HasValue && (z1Th >= 1 || z1Th <= 0)) ||
                (z2Th.HasValue && (z2Th >= 1 || z2Th <= 0)) ||
                (z3Th.HasValue && (z3Th >= 1 || z3Th <= 0)))
            {
                throw new ArgumentOutOfRangeException("Each threshold value must be between 0 and 1");
            }
            if (population <= 0) throw new ArgumentOutOfRangeException("The population must have at least one element");
            if (weightsMutationRate <= 0 || weightsMutationRate > 99) throw new ArgumentOutOfRangeException("The mutation rate must be between 0 and 100");
            if (eliteSamples < 0 || eliteSamples >= population)
            {
                throw new ArgumentOutOfRangeException("The number of elite samples must be a positive number less than or equal to the population size");
            }

            // Assign the fields
            FitnessFunction = fitnessFunction;
            InputLayerSize = input;
            OutputLayerSize = output;
            FirstHiddenLayerSize = firstHiddenSize;
            SecondHiddenLayerSize = secondHiddenSize;
            Z1Threshold = z1Th;
            Z2Threshold = z2Th;
            Z3Threshold = z3Th;
            PopulationSize = population;
            WeightsMutationRate = weightsMutationRate;
            EliteSamples = eliteSamples;

        }

        /// <summary>
        /// Creates a new provider instance with a single hidden neurons layer
        /// </summary>
        /// <param name="fitnessFunction">The fitness function used to evaluate the neural networks</param>
        /// <param name="input">Number of inputs in the neural network</param>
        /// <param name="output">Number of outputs in the neural network</param>
        /// <param name="size">Number of neurons in the hidden layer</param>
        /// <param name="z1Threshold">Optional threshold in the hidden layer neurons</param>
        /// <param name="z2Threshold">Optional threshold in the output layer</param>
        /// <param name="population">Number of networks in the population</param>
        /// <param name="weightsMutationRate">Probability for each weight mutation</param>
        /// <param name="eliteSamples">Number of best networks to copy in each generation</param>
        public static Task<NeuralNetworkGeneticAlgorithmProvider> NewSingleLayerAsync(
            FitnessDelegate fitnessFunction,
            int input, int output, int size, double? z1Threshold, double? z2Threshold,
            int population, int weightsMutationRate, int eliteSamples)
        {
            return Task.Run(() =>
            {
                NeuralNetworkGeneticAlgorithmProvider provider = new NeuralNetworkGeneticAlgorithmProvider(fitnessFunction,
                    input, output, size, 0, z1Threshold, z2Threshold, null, population, weightsMutationRate, eliteSamples);
                provider._Population = provider.InitializePopulation();
                return provider;
            });
        }

        /// <summary>
        /// Creates a new provider instance with a single hidden neurons layer
        /// </summary>
        /// <param name="fitnessFunction">The fitness function used to evaluate the neural networks</param>
        /// <param name="input">Number of inputs in the neural network</param>
        /// <param name="output">Number of outputs in the neural network</param>
        /// <param name="firstHiddenSize">Number of neurons in the first hidden layer</param>
        /// <param name="secondHiddenSize">Number of neurons in the second hidden layer</param>
        /// <param name="z1Threshold">Optional threshold in the first hidden layer neurons</param>
        /// <param name="z2Threshold">Optional threshold in the second hidden layer</param>
        /// <param name="z3Threshold">Optional threshold in the output layer</param>
        /// <param name="population">Number of networks in the population</param>
        /// <param name="weightsMutationRate">Probability for each weight mutation</param>
        /// <param name="eliteSamples">Number of best networks to copy in each generation</param>
        public static Task<NeuralNetworkGeneticAlgorithmProvider> NewTwoLayersLayerAsync(
            FitnessDelegate fitnessFunction,
            int input, int output, int firstHiddenSize, int secondHiddenSize, double? z1Threshold, double? z2Threshold, double? z3Threshold,
            int population, int weightsMutationRate, int eliteSamples)
        {
            return Task.Run(() =>
            {
                NeuralNetworkGeneticAlgorithmProvider provider = new NeuralNetworkGeneticAlgorithmProvider(fitnessFunction,
                    input, output, firstHiddenSize, secondHiddenSize, z1Threshold, z2Threshold, z3Threshold, population, weightsMutationRate, eliteSamples);
                provider._Population = provider.InitializePopulation();
                return provider;
            });
        }

        #region Pre-initialized providers

        // Helper method to get a provider instance
        private static NeuralNetworkGeneticAlgorithmProvider ReconstructInstance(
            FitnessDelegate fitnessFunction, NeuralNetworkBase network,
            int population, int weightsMutationRate, int eliteSamples)
        {
            // Reconstruct the original data
            NeuralNetworkGeneticAlgorithmProvider provider;
            if (network is TwoLayersNeuralNetwork)
            {
                TwoLayersNeuralNetwork twoLayers = (TwoLayersNeuralNetwork)network;
                provider = new NeuralNetworkGeneticAlgorithmProvider(
                    fitnessFunction, twoLayers.InputLayerSize, twoLayers.OutputLayerSize, twoLayers.HiddenLayerSize,
                    twoLayers.SecondHiddenLayerSize, twoLayers.Z1Threshold, twoLayers.Z2Threshold,
                    twoLayers.Z3Threshold,
                    population, weightsMutationRate, eliteSamples);
            }
            else
            {
                provider = new NeuralNetworkGeneticAlgorithmProvider(
                    fitnessFunction, network.InputLayerSize, network.OutputLayerSize, network.HiddenLayerSize,
                    0, network.Z1Threshold, network.Z2Threshold, null, population, weightsMutationRate, eliteSamples);
            }

            // Randomize the population
            NeuralNetworkBase[] initialPopulation = new NeuralNetworkBase[population];
            initialPopulation[0] = network;
            for (int i = 1; i < population - 1; i++) initialPopulation[i] = provider.MutateNetwork(network);
            return provider;
        }

        /// <summary>
        /// Creates a new instance from a serialized neural network
        /// </summary>
        /// <param name="fitnessFunction">The fitness function used to evaluate the neural networks</param>
        /// <param name="networkData">The serialized neural network to use to initialize the provider</param>
        /// <param name="population">Number of networks in the population</param>
        /// <param name="weightsMutationRate">Probability for each weight mutation</param>
        /// <param name="eliteSamples">Number of best networks to copy in each generation</param>
        public static Task<NeuralNetworkGeneticAlgorithmProvider> FromSerializedNetworkAsync(
            FitnessDelegate fitnessFunction, byte[] networkData,
            int population, int weightsMutationRate, int eliteSamples)
        {
            return Task.Run(() =>
            {
                // Try to deserialize the original network
                NeuralNetworkBase network = NeuralNetworkDeserializer.TryGetInstance(networkData) as NeuralNetworkBase;
                return network == null ? null : ReconstructInstance(fitnessFunction, network, population, weightsMutationRate, eliteSamples);
            });
        }

        /// <summary>
        /// Creates a new instance from a serialized neural network
        /// </summary>
        /// <param name="fitnessFunction">The fitness function used to evaluate the neural networks</param>
        /// <param name="network">The neural network to use to initialize the provider</param>
        /// <param name="population">Number of networks in the population</param>
        /// <param name="weightsMutationRate">Probability for each weight mutation</param>
        /// <param name="eliteSamples">Number of best networks to copy in each generation</param>
        public static Task<NeuralNetworkGeneticAlgorithmProvider> FromNetworkAsync(
            FitnessDelegate fitnessFunction, INeuralNetwork network,
            int population, int weightsMutationRate, int eliteSamples)
        {
            return Task.Run(() => ReconstructInstance(fitnessFunction, (NeuralNetworkBase)network, population, weightsMutationRate, eliteSamples));
        }

        #endregion

        #endregion

        #region Public methods

        /// <summary>
        /// Starts the provider, returns true if the operation is successful
        /// </summary>
        public async Task<bool> StartAsync()
        {
            // Wait and check the current status
            await RunningSemaphore.WaitAsync();
            if (_Cts != null)
            {
                RunningSemaphore.Release();
                return false;
            }

            // Start the genetic algorithm
            _Cts = new CancellationTokenSource();
            BreedNetworks(_Cts.Token);
            RunningSemaphore.Release();
            return true;
        }

        /// <summary>
        /// Stops the provider, returns false if it wasn't running when the method was called
        /// </summary>
        public async Task<bool> StopAsync()
        {
            // Wait and check if the provider was running
            await RunningSemaphore.WaitAsync();
            if (_Cts == null)
            {
                RunningSemaphore.Release();
                return false;
            }

            // Stop the genetic algorithm
            _Cts.Cancel();
            _Cts = null;
            RunningSemaphore.Release();
            return true;
        }

        #endregion

        #region Genetic algorithm

        /// <summary>
        /// Initializes a new random population with the current parameters
        /// </summary>
        private NeuralNetworkBase[] InitializePopulation()
        {
            // Single layer neural network
            NeuralNetworkBase[] population = new NeuralNetworkBase[PopulationSize];
            if (SecondHiddenLayerSize == 0)
            {
                for (int i = 0; i < PopulationSize; i++)
                {
                    population[i] = new NeuralNetwork(InputLayerSize, OutputLayerSize, FirstHiddenLayerSize, Z1Threshold, Z2Threshold, RandomProvider);
                }
            }
            else
            {
                // Two layers if needed
                for (int i = 0; i < PopulationSize; i++)
                {
                    population[i] = new TwoLayersNeuralNetwork(InputLayerSize, OutputLayerSize, FirstHiddenLayerSize,
                        SecondHiddenLayerSize, Z1Threshold, Z2Threshold, Z3Threshold, RandomProvider);
                }
            }
            return population;
        }

        /// <summary>
        /// Returns a new mutated network from the input network
        /// </summary>
        /// <param name="network">The input network</param>
        private NeuralNetworkBase MutateNetwork(NeuralNetworkBase network)
        {
            // Iterate over all the layers
            Random r = new Random(network.GetHashCode());
            if (network.GetType() == typeof(NeuralNetwork))
            {
                Tuple<double[,], double[,]> weights = ((NeuralNetwork)network).Weights;
                foreach (double[,] weight in new[] { weights.Item1, weights.Item2 })
                {
                    MatrixHelper.RandomMutate(weight, WeightsMutationRate, r);
                }
                return new NeuralNetwork(network.InputLayerSize, network.OutputLayerSize, network.HiddenLayerSize, weights.Item1, weights.Item2, Z1Threshold, Z2Threshold);
            }
            else
            {
                // Double neural network
                TwoLayersNeuralNetwork twoLayersNet = (TwoLayersNeuralNetwork)network;
                Tuple<double[,], double[,], double[,]> weights = twoLayersNet.Weights;
                foreach (double[,] weight in new[] { weights.Item1, weights.Item2, weights.Item3 })
                {
                    MatrixHelper.RandomMutate(weight, WeightsMutationRate, r);
                }
                return new TwoLayersNeuralNetwork(network.InputLayerSize, network.OutputLayerSize, network.HiddenLayerSize,
                    twoLayersNet.SecondHiddenLayerSize, weights.Item1, weights.Item2, weights.Item3,
                    Z1Threshold, Z2Threshold, Z3Threshold);
            }
        }

        /// <summary>
        /// Runs the genetic algorithm
        /// </summary>
        /// <param name="token">The cancellation token for the algorithm</param>
        private async void BreedNetworks(CancellationToken token)
        {
            // Loop until the token is cancelled
            while (!token.IsCancellationRequested)
            {
                // Test the current generation
                IEnumerable<Task<Tuple<NeuralNetworkBase, double>>> testing = _Population.Select(async net =>
                {
                    double fitness = await Task.Run(() => FitnessFunction(net.GetHashCode(), net.Forward));
                    return Tuple.Create(net, fitness);
                });
                Tuple<NeuralNetworkBase, double>[] result = await Task.WhenAll(testing);

                // Iterate over all the results
                double tot = 0;
                Tuple<NeuralNetworkBase, double> bestResult = null;
                foreach (Tuple<NeuralNetworkBase, double> res in result)
                {
                    // Get the best score and the total
                    if (bestResult == null || res.Item2 > bestResult.Item2)
                    {
                        bestResult = res;
                    }
                    tot += res.Item2;
                }
                if (bestResult == null) throw new InvalidOperationException();
                if (bestResult.Item2 > BestFitness) BestResult = bestResult;

                // Invoke the callback if possible
                ProgressCallback?.Invoke(new GeneticAlgorithmProgress(Generation, bestResult.Item2, tot / PopulationSize, BestFitness));
                Generation++;

                // Iterate over the results and populate the mating pool
                Tuple<NeuralNetworkBase, double>[] matingPool = new Tuple<NeuralNetworkBase, double>[PopulationSize];
                for (int i = 0; i < PopulationSize; i++)
                {
                    // Pick a random mate
                    int b;
                    do
                    {
                        b = RandomProvider.Next(PopulationSize);
                    } while (i == b);

                    // Add the best one to the pool
                    matingPool[i] = result[i].Item2 > result[b].Item2 ? result[i] : result[b];
                }

                // Initialize the children list and select the elite
                List<NeuralNetworkBase> children = new List<NeuralNetworkBase>();
                children.AddRange(matingPool.OrderByDescending(r => r.Item2).Take(EliteSamples).Select(r => r.Item1));

                // Filter the mating pool to skip the worst results
                NeuralNetworkBase[] filtered = matingPool.OrderBy(r => r.Item2).Skip(EliteSamples).Select(r => r.Item1).ToArray();
                for (int i = 0; i < filtered.Length; i++)
                {
                    // Select the parents for the new child
                    int a, b;
                    do
                    {
                        a = RandomProvider.Next(filtered.Length);
                        b = RandomProvider.Next(filtered.Length);
                    } while (a == b);

                    // Two points crossover
                    children.Add(filtered[a].Crossover(filtered[b], RandomProvider));
                }
                if (children.Count != PopulationSize) Debugger.Break();

                // Queue and run all the current mutation
                IEnumerable<Task<NeuralNetworkBase>> mutation = children.Select(child => Task.Run(() => MutateNetwork(child)));
                _Population = await Task.WhenAll(mutation);
            }
        }

        #endregion
    }
}