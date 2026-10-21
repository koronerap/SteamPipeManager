package io.github.koronerap.pulse.data

import java.security.MessageDigest

/**
 * PBKDF2-HMAC-SHA256, tek blok (32 bayt), hızlı. Telefonda 600.000 tur:
 * - Android'in hazır "PBKDF2WithHmacSHA256"ı saf Java (BouncyCastle), onlarca saniye;
 * - her turda yerel HmacSHA256 çağırmak (Conscrypt) da 19 saniye — süre şifrelemede değil,
 *   her çağrının JNI ve bellek yükünde.
 * Burada HMAC'in iç ve dış durumları bir kez hesaplanıyor; her tur tam iki SHA-256
 * sıkıştırması, bellek ayırmadan. Sonuç standart PBKDF2 ile aynı (testler masaüstünün
 * .NET çıktısıyla ve Java'nın kendi gerçeklemesiyle karşılaştırıyor).
 */
internal object Pbkdf2 {
    fun sha256(password: ByteArray, salt: ByteArray, iterations: Int): ByteArray {
        require(iterations >= 1)
        val key = if (password.size > 64) MessageDigest.getInstance("SHA-256").digest(password) else password

        val ipad = ByteArray(64) { i -> ((if (i < key.size) key[i].toInt() else 0) xor 0x36).toByte() }
        val opad = ByteArray(64) { i -> ((if (i < key.size) key[i].toInt() else 0) xor 0x5c).toByte() }

        val w = IntArray(64)
        val inner = IV.copyOf().also { compress(it, words(ipad), w) }
        val outer = IV.copyOf().also { compress(it, words(opad), w) }

        // İlk tur: U1 = HMAC(parola, tuz || INT(1)); genel uzunlukta, standart özetle.
        val sha = MessageDigest.getInstance("SHA-256")
        sha.update(ipad)
        sha.update(salt)
        sha.update(byteArrayOf(0, 0, 0, 1))
        val innerDigest = sha.digest()
        sha.update(opad)
        sha.update(innerDigest)

        val u = words(sha.digest())
        val result = u.copyOf()
        val block = IntArray(16)
        val state = IntArray(8)

        // Sonraki turlar: 32 baytlık mesaj, dolgusu sabit (64 + 32 bayt = 768 bit).
        repeat(iterations - 1) {
            u.copyInto(block, 0, 0, 8)
            pad(block)
            inner.copyInto(state)
            compress(state, block, w)

            state.copyInto(block, 0, 0, 8)
            pad(block)
            outer.copyInto(u)
            compress(u, block, w)

            for (i in 0 until 8) {
                result[i] = result[i] xor u[i]
            }
        }

        return ByteArray(32) { i -> (result[i / 4] ushr (24 - 8 * (i % 4))).toByte() }
    }

    private fun pad(block: IntArray) {
        block[8] = 0x80000000.toInt()

        for (i in 9 until 15) {
            block[i] = 0
        }

        block[15] = (64 + 32) * 8
    }

    private fun words(bytes: ByteArray): IntArray = IntArray(bytes.size / 4) { i ->
        ((bytes[4 * i].toInt() and 0xff) shl 24) or ((bytes[4 * i + 1].toInt() and 0xff) shl 16) or
            ((bytes[4 * i + 2].toInt() and 0xff) shl 8) or (bytes[4 * i + 3].toInt() and 0xff)
    }

    private fun compress(state: IntArray, block: IntArray, w: IntArray) {
        for (i in 0 until 16) {
            w[i] = block[i]
        }

        for (i in 16 until 64) {
            val s0 = Integer.rotateRight(w[i - 15], 7) xor Integer.rotateRight(w[i - 15], 18) xor (w[i - 15] ushr 3)
            val s1 = Integer.rotateRight(w[i - 2], 17) xor Integer.rotateRight(w[i - 2], 19) xor (w[i - 2] ushr 10)
            w[i] = w[i - 16] + s0 + w[i - 7] + s1
        }

        var a = state[0]
        var b = state[1]
        var c = state[2]
        var d = state[3]
        var e = state[4]
        var f = state[5]
        var g = state[6]
        var h = state[7]

        for (i in 0 until 64) {
            val s1 = Integer.rotateRight(e, 6) xor Integer.rotateRight(e, 11) xor Integer.rotateRight(e, 25)
            val ch = (e and f) xor (e.inv() and g)
            val t1 = h + s1 + ch + K[i] + w[i]
            val s0 = Integer.rotateRight(a, 2) xor Integer.rotateRight(a, 13) xor Integer.rotateRight(a, 22)
            val maj = (a and b) xor (a and c) xor (b and c)
            val t2 = s0 + maj

            h = g
            g = f
            f = e
            e = d + t1
            d = c
            c = b
            b = a
            a = t1 + t2
        }

        state[0] += a
        state[1] += b
        state[2] += c
        state[3] += d
        state[4] += e
        state[5] += f
        state[6] += g
        state[7] += h
    }

    private val IV = longArrayOf(
        0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19,
    ).map(Long::toInt).toIntArray()

    private val K = longArrayOf(
        0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
        0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
        0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
        0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
        0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
        0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
        0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
        0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
    ).map(Long::toInt).toIntArray()
}
