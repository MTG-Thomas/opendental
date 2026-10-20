FROM mcr.microsoft.com/dotnet/sdk:8.0
WORKDIR /work
COPY src/ src/
COPY vectors.json .
RUN dotnet build src/Verify/Verify.csproj -c Release --nologo -v q
ENTRYPOINT ["dotnet", "src/Verify/bin/Release/net8.0/Verify.dll", "vectors.json"]
