# UrbanHealth: Sistema Distribuído de Monitorização Urbana

Este projeto consiste numa solução completa de monitorização de saúde urbana baseada em sistemas distribuídos. Utiliza uma arquitetura em camadas (Edge to Cloud) para recolha, processamento e visualização de dados de sensores.

---

## Arquitetura do Sistema

O sistema está dividido em três componentes principais:

1.  **Sensores:** Geram dados (Temperatura, Humidade, Qualidade do Ar) e enviam para o Gateway via TCP/UDP.
2.  **Gateway:** Agrega os dados dos sensores locais, gere o buffering (Store-and-Forward) e encaminha as leituras para o Servidor.
3.  **Servidor Central (Cloud):** Alojado em Docker na AWS, processa os dados, gere a base de dados SQLite e disponibiliza um Dashboard Web em tempo real.

---

## ☁️ Acesso Remoto e Nuvem (AWS)

O servidor central está alojado numa instância **EC2 (Ubuntu 24.04 LTS)**.

* **IP Público:** `16.171.143.55`
* **Comando de Acesso SSH:**
    ```bash
    ssh -i "chave-sd.pem" ubuntu@16.171.143.55
    ```

> **Nota para Windows:** Se a chave `.pem` der erro de permissões, executa no PowerShell:
> ```powershell
> icacls "chave-sd.pem" /inheritance:r
> icacls "chave-sd.pem" /grant:r "$($env:USERNAME):F"
> ```

---

## Gestão do Servidor (Docker)

O servidor utiliza Docker Compose para garantir a persistência dos dados e facilidade de deploy.

### Comandos Essenciais (Na AWS):
* **Iniciar/Atualizar o Servidor:**
    ```bash
    docker-compose up --build -d
    ```
* **Ver Logs em Tempo Real:**
    ```bash
    docker logs -f urban-server-central
    ```
* **Parar o Sistema:**
    ```bash
    docker-compose down
    ```


---

## Como Executar Localmente

### 1. Iniciar o Gateway
O Gateway deve ser iniciado apontando para o IP do Servidor (Local ou AWS).
```bash
cd Gateway
dotnet run -- G101 51.21.182.118