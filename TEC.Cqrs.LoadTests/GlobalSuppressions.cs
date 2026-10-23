using System.Diagnostics.CodeAnalysis;

// Random é intencional aqui: sorteia as operações da carga sintética (com semente, para execuções reproduzíveis).
// Não há uso de segurança (tokens, senhas, chaves), então um gerador criptográfico não se aplica.
[assembly: SuppressMessage("Security", "CA5394:Do not use insecure randomness",
    Justification = "Carga sintética reproduzível: Random sorteia cenários e dados de teste, sem finalidade de segurança.")]
