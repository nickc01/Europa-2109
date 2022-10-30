static const int numThreads = 8;

RWStructuredBuffer<float> points;
int numPointsPerAxis;
float boundsSize;
float3 centre;
float3 offset;
float spacing;
float3 worldSize;

int indexFromCoord(uint x, uint y, uint z) {
    return (z * numPointsPerAxis * numPointsPerAxis) + (y * numPointsPerAxis) + x;
}