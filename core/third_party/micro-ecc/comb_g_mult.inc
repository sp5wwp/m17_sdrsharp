/* ============================================================================
   Fixed-base constant-time comb multiplication for k*G, for use ONLY in the
   signing path's ephemeral-nonce multiplication (the one operation where the
   scalar is secret and timing must not depend on it).

   Design notes:
   - W=4 bit comb, T_COLS=67 columns -> 66 doublings + 67 table adds, instead
     of the ladder's 257 doublings.
   - Table entries are pre-blinded by a fixed public point B so no table entry
     is ever the point at infinity (avoids a data-dependent branch that a
     naive comb implementation would need for "digit == 0" columns - that
     branch would leak nonce bits). The fixed contribution of B across the
     whole comb is removed at the end by subtracting the public correction
     point C = (2^T_COLS - 1)*B.
   - Table lookups are done via a constant-time masked scan over all 16
     entries (comb_table_select), not indexed array access.
   - The scalar passed in MUST be pre-blinded by the caller as
     k_blinded = k + r*n for a FRESH random r each call (see
     compute_blinded_nonce_scalar). This is what restores the per-signature
     re-randomization that EccPoint_mult's initial_Z parameter used to
     provide, the additive point-blinding by B alone is a FIXED constant and
     provides no differential-power-analysis protection by itself.
   ============================================================================ */

#if uECC_COMB_MULT && uECC_WORD_SIZE != 4
   #error "comb_g_mult.inc requires 32-bit words; build with -DuECC_COMB_MULT=0"
#endif

#define COMB_W 4
#define COMB_T_COLS 67
#define COMB_SCALAR_WORDS 9      /* holds up to 288 bits, 268 are meaningful */
#define COMB_RAND_BYTES 1        /* r in [0,255], 12 bits of headroom exist, we use 8 */

/* T'[idx] = B + sum_{i: bit i of idx set} (2^(i*67) * G), idx = 0..15, affine (x||y).
   C = (2^67 - 1) * B. Generated offline for secp256r1 by gen_comb_table.py.
   verified against an independent field-arithmetic model before being pasted
   here. Do not hand-edit - regenerate from the script if parameters change. */
static const uECC_word_t comb_table_secp256r1[16][16] = {
    { 0xC9406ECA, 0xF33630DA, 0x502313F0, 0xDCECBDD3, 0x772E8D54, 0x496AFE16, 0x7F8B2E78, 0x83F93D3C,
      0xF562D4C6, 0x6EA67C71, 0x9F80AEEF, 0x57776CF8, 0x059D75AD, 0x9AA606F9, 0xADB19019, 0x9FCDC295 },
    { 0x1960EF0F, 0x2B0A4710, 0x3F5D4A3B, 0x955E07A2, 0x2443F65B, 0xE3DD396C, 0xBADBB986, 0x616E9CD5,
      0xCC1D7DDF, 0x75166836, 0xB13BA5D5, 0xD769F918, 0xB02E698C, 0xC55407A8, 0x2CC4EA0E, 0x957A31B4 },
    { 0x0BF5192C, 0xED353CF4, 0x6DD089F9, 0x06D9182F, 0x42A94C79, 0x00658CB1, 0x26F85EC2, 0xA1424A0B,
      0xED9D641E, 0xF0DF7B95, 0x618CA968, 0x573AB4BB, 0x56406396, 0xB06291D0, 0x68A61690, 0x9ED69361 },
    { 0xF040014F, 0x1F974C67, 0x8DE32C82, 0x64BCDBB7, 0x1A62C344, 0xC8BDB3A6, 0x64536538, 0x88AFCB18,
      0x8AD658EF, 0x7AE90BAE, 0x5E84CAA2, 0xEBF27D32, 0x1513F378, 0xC2ED6A82, 0xEB77DD07, 0x28FDE9F2 },
    { 0x3F669C1A, 0xB3764794, 0x2335B391, 0x6BA84595, 0x88C7737E, 0xE9D7AB07, 0xC163E0B5, 0xF64C3AF5,
      0xA08B7A49, 0x70B5BAAA, 0x37AA8622, 0x23602150, 0x323A2BF5, 0x22EC323B, 0x41C1FD01, 0x82B06522 },
    { 0xD0D12C38, 0x29E4F3CD, 0x8430D0F8, 0x1E1FF0A4, 0xBED559D8, 0x6C773F04, 0xEAAFA70A, 0x3E44F132,
      0x33FE78A4, 0xF42A79EA, 0x00C22D1F, 0xF9703DF2, 0x34C10BB2, 0xE732C21B, 0xB2AC1F15, 0x1BB44C8A },
    { 0x66FBE397, 0x89FDCF74, 0x5B836DB2, 0x00942570, 0xC1BE4B83, 0x3DBA0277, 0xB9A18779, 0x3DFD6ADE,
      0xE30D1393, 0xCEF8E6CC, 0xCB2C7E79, 0xF5286903, 0xB340CE0E, 0x89A5A814, 0xD8D131FB, 0xABB58E93 },
    { 0x7806B242, 0xB00FB452, 0xE3A9E1AF, 0x1B11B24E, 0x8DB7E265, 0x944D5589, 0xB365F388, 0x036B40ED,
      0x173DFBB9, 0x7DC41E41, 0x21D8C89B, 0xAFBD1FA0, 0x7E7BCFD8, 0x06F5BB8C, 0x05F644E8, 0x857E11C4 },
    { 0x494D78C9, 0x1FAAE0D8, 0x0CBDE10A, 0x38D7F75C, 0xDED238D6, 0x3F9590C0, 0xB9E4DE41, 0x75CA05E1,
      0x09A3D953, 0x4E37477E, 0x579E9F36, 0x89B242AC, 0x1FFD02FD, 0xD0A508F3, 0x373EB889, 0xE63280D2 },
    { 0x3A7879FC, 0x4E98C495, 0xEEAEC727, 0x6EBDCD84, 0x2AE35CEC, 0xB7851F2B, 0x68B1C565, 0x62C542D5,
      0x7681195C, 0x4D5A7E07, 0x315714F2, 0x621152CB, 0x25B7CEC4, 0x8B58380E, 0x39910793, 0xE0B6D1B0 },
    { 0xD712A8B3, 0x4F20BC17, 0x03EA3089, 0x98ACD074, 0x478E3C26, 0x7C8B2F2F, 0x14188072, 0x87D7C4DF,
      0xC52CF35A, 0x87D16DA2, 0xC3EE17BA, 0xF358B145, 0x724A47B8, 0x20D9A539, 0x72FEA250, 0x872C3789 },
    { 0xD7116804, 0x089B876D, 0xDBF4D22B, 0xBEE24A37, 0x030B5EF6, 0x109C6995, 0x6764F749, 0x4442CD89,
      0x9E9326F0, 0x8CB7BD73, 0xDC13E4B5, 0x73154D6D, 0xB0B06D39, 0xABCCFA06, 0x188FF828, 0x6B63793C },
    { 0xB684F10C, 0xBB3E37FF, 0x0F35AE13, 0x7146A442, 0xCB2D44D6, 0xB8CB0F4E, 0xC7CB454D, 0x001A8A01,
      0x0D10CF29, 0x82F73F4D, 0x2D9200D5, 0x2F1F2ADC, 0x664B2FB6, 0x30597C39, 0x926BDE6A, 0x6AA9D315 },
    { 0x46064A60, 0x3EC35146, 0xE4B0F191, 0xC2A5B5C6, 0xA974C99D, 0xB674C255, 0x6E72F49C, 0xED97E59F,
      0x258F4DC8, 0xB395A8B6, 0x44A8DBBE, 0xB65024BA, 0x064F955C, 0x3228B756, 0x02550550, 0xF72947AF },
    { 0x71205AE8, 0x73B0A0F9, 0x8FB3BEAA, 0x8BB64E21, 0x86E0B7BA, 0xD7096F17, 0xB3FF7329, 0xE57EA22D,
      0xAA106D99, 0xF2E49329, 0x00A490AD, 0xF1BB5133, 0x7D0286B3, 0x849B75E6, 0x81A0820B, 0x92CF85E9 },
    { 0xBB15CDB2, 0xA2109A6D, 0x7E4A8DB1, 0x2D776702, 0xCE986A9E, 0xB50395E0, 0xE3704D5B, 0x9C558BC2,
      0x7E46C9D0, 0xFE7840DC, 0x8297394D, 0x9CABE1AA, 0xB005ACBA, 0x01235026, 0x145BEFB5, 0xB17EBE73 }
};

static const uECC_word_t comb_correction_secp256r1[16] =
    { 0x2DB5F2C6, 0x216AFC24, 0xB5BD6A1B, 0xA501BC29, 0x88296D2E, 0xD5F4F115, 0xB5BB64A3, 0x9C29564C,
      0xBB4CC81F, 0xFEA18965, 0x3106FEE0, 0xEC2B0AD5, 0x8F72B37B, 0xE3ACE169, 0xA0BFCD7A, 0x12591866 };

/* returns all-1-bits if a==b, all-0-bits otherwise, branch-free */
static uECC_word_t ct_eq_mask(uECC_word_t a, uECC_word_t b) {
    uECC_word_t d = a ^ b;
    uECC_word_t nz = d | (uECC_word_t)(0 - d);        /* MSB set iff d != 0 */
    uECC_word_t not_equal_mask = (uECC_word_t)0 - (nz >> (uECC_WORD_BITS - 1));
    return ~not_equal_mask;
}

/* Constant-time table lookup: touches every entry every time, blends with a
   mask rather than branching or indexing by the secret digit. */
static void comb_table_select(uECC_word_t *x_out, uECC_word_t *y_out,
                              uECC_word_t digit, wordcount_t num_words) {
    wordcount_t i, j;
    for (j = 0; j < num_words; ++j) {
        x_out[j] = 0;
        y_out[j] = 0;
    }
    for (i = 0; i < 16; ++i) {
        uECC_word_t mask = ct_eq_mask((uECC_word_t)i, digit);
        const uECC_word_t *tx = &comb_table_secp256r1[i][0];
        const uECC_word_t *ty = &comb_table_secp256r1[i][num_words];
        for (j = 0; j < num_words; ++j) {
            x_out[j] |= (tx[j] & mask);
            y_out[j] |= (ty[j] & mask);
        }
    }
}

/* Extracts the 4-bit comb digit for column 'col' from a COMB_SCALAR_WORDS-word
   scalar. Bit positions read are public (loop/column indices), only the bit
   VALUES are secret, so this has no data-dependent control flow. */
static uECC_word_t comb_digit(const uECC_word_t *k, int col) {
    uECC_word_t digit = 0;
    int i;
    for (i = 0; i < COMB_W; ++i) {
        int bitpos = i * COMB_T_COLS + col;
        uECC_word_t raw = k[bitpos >> uECC_WORD_BITS_SHIFT] &
                           ((uECC_word_t)1 << (bitpos & uECC_WORD_BITS_MASK));
        uECC_word_t bit = raw >> (bitpos & uECC_WORD_BITS_MASK);
        digit |= (bit << i);
    }
    return digit;
}

/* Jacobian(X1,Y1,Z1) += affine(x2,y2). madd-2007-bl, unmodified/non-complete:
   assumes Z1 != 0 and the two points are not equal or negatives of each
   other (true except with probability ~2^-256 for random inputs here,
   the same standard assumption non-complete-formula EC code always makes). */
static void mixed_add_jacobian_affine(uECC_word_t *X1, uECC_word_t *Y1, uECC_word_t *Z1,
                                      const uECC_word_t *x2, const uECC_word_t *y2,
                                      uECC_Curve curve) {
    uECC_word_t t1[uECC_MAX_WORDS];
    uECC_word_t t2[uECC_MAX_WORDS];
    uECC_word_t t3[uECC_MAX_WORDS];
    uECC_word_t t4[uECC_MAX_WORDS];
    uECC_word_t t5[uECC_MAX_WORDS];
    uECC_word_t t6[uECC_MAX_WORDS];
    uECC_word_t t7[uECC_MAX_WORDS];
    uECC_word_t t8[uECC_MAX_WORDS];
    uECC_word_t t9[uECC_MAX_WORDS];
    wordcount_t num_words = curve->num_words;
    const uECC_word_t *p = curve->p;

    uECC_vli_modSquare_fast(t1, Z1, curve);      /* t1 = Z1Z1 */
    uECC_vli_modMult_fast(t2, x2, t1, curve);    /* t2 = U2 = x2*Z1Z1 */
    uECC_vli_modMult_fast(t3, t1, Z1, curve);    /* t3 = Z1^3 */
    uECC_vli_modMult_fast(t3, y2, t3, curve);    /* t3 = S2 = y2*Z1^3 */
    uECC_vli_modSub(t4, t2, X1, p, num_words);   /* t4 = H = U2 - X1 */
    uECC_vli_modSquare_fast(t5, t4, curve);      /* t5 = HH = H^2 */
    uECC_vli_modAdd(t6, t5, t5, p, num_words);   /* t6 = 2HH */
    uECC_vli_modAdd(t6, t6, t6, p, num_words);   /* t6 = I = 4HH */
    uECC_vli_modMult_fast(t7, t4, t6, curve);    /* t7 = J = H*I */
    uECC_vli_modSub(t8, t3, Y1, p, num_words);   /* t8 = S2-Y1 */
    uECC_vli_modAdd(t8, t8, t8, p, num_words);   /* t8 = r = 2*(S2-Y1) */
    uECC_vli_modMult_fast(t9, X1, t6, curve);    /* t9 = V = X1*I */

    uECC_vli_modSquare_fast(t1, t8, curve);      /* t1 = r^2 (reuse) */
    uECC_vli_modSub(t1, t1, t7, p, num_words);   /* t1 = r^2 - J */
    uECC_vli_modAdd(t2, t9, t9, p, num_words);   /* t2 = 2V (reuse) */
    uECC_vli_modSub(t1, t1, t2, p, num_words);   /* t1 = X3 = r^2 - J - 2V */

    uECC_vli_modSub(t2, t9, t1, p, num_words);   /* t2 = V - X3 (reuse) */
    uECC_vli_modMult_fast(t2, t8, t2, curve);    /* t2 = r*(V-X3) */
    uECC_vli_modMult_fast(t3, Y1, t7, curve);    /* t3 = Y1*J (reuse) */
    uECC_vli_modAdd(t3, t3, t3, p, num_words);   /* t3 = 2*Y1*J */
    uECC_vli_modSub(t2, t2, t3, p, num_words);   /* t2 = Y3 = r*(V-X3) - 2*Y1*J */

    uECC_vli_modMult_fast(Z1, Z1, t4, curve);    /* Z1 = Z1*H */
    uECC_vli_modAdd(Z1, Z1, Z1, p, num_words);   /* Z3 = 2*Z1*H */

    uECC_vli_set(X1, t1, num_words);
    uECC_vli_set(Y1, t2, num_words);
}

/* Computes result = k_blinded*G in constant time. k_blinded must already be
   Coron-style re-randomized (see compute_blinded_nonce_scalar) - this
   function on its own only fixes the timing/branch problem, not DPA. */
static void EccPoint_mult_G_comb(uECC_word_t *result,
                                 const uECC_word_t *k_blinded,
                                 uECC_Curve curve) {
    uECC_word_t X[uECC_MAX_WORDS];
    uECC_word_t Y[uECC_MAX_WORDS];
    uECC_word_t Z[uECC_MAX_WORDS];
    uECC_word_t digit;
    wordcount_t num_words = curve->num_words;
    int col;

    digit = comb_digit(k_blinded, COMB_T_COLS - 1);
    comb_table_select(X, Y, digit, num_words);
    uECC_vli_clear(Z, num_words);
    Z[0] = 1;

    for (col = COMB_T_COLS - 2; col >= 0; --col) {
        uECC_word_t tx[uECC_MAX_WORDS];
        uECC_word_t ty[uECC_MAX_WORDS];
        curve->double_jacobian(X, Y, Z, curve);
        digit = comb_digit(k_blinded, col);
        comb_table_select(tx, ty, digit, num_words);
        mixed_add_jacobian_affine(X, Y, Z, tx, ty, curve);
    }

    /* subtract the fixed public correction point C */
    {
        uECC_word_t negCy[uECC_MAX_WORDS];
        uECC_vli_sub(negCy, curve->p, &comb_correction_secp256r1[num_words], num_words);
        mixed_add_jacobian_affine(X, Y, Z, comb_correction_secp256r1, negCy, curve);
    }

    /* Jacobian -> affine */
    {
        uECC_word_t zi[uECC_MAX_WORDS];
        uECC_word_t zi2[uECC_MAX_WORDS];
        uECC_vli_modInv(zi, Z, curve->p, num_words);
        uECC_vli_modSquare_fast(zi2, zi, curve);
        uECC_vli_modMult_fast(result, X, zi2, curve);
        uECC_vli_modMult_fast(zi, zi2, zi, curve);
        uECC_vli_modMult_fast(result + num_words, Y, zi, curve);
    }
}

/* Coron-style per-call scalar re-randomization: k_blinded = k + r*n, r fresh
   random each call. n*G == O so this doesn't change the result, but it
   restores the per-signature decorrelation that the ladder's random
   initial_Z used to provide (the additive point-blinding by B above is a
   FIXED constant and gives no DPA protection on its own). */
static int compute_blinded_nonce_scalar(uECC_word_t *k_blinded /* COMB_SCALAR_WORDS */,
                                        const uECC_word_t *k,
                                        uECC_Curve curve) {
    uECC_word_t r_padded[uECC_MAX_WORDS];
    uECC_word_t product[2 * uECC_MAX_WORDS];
    wordcount_t num_n_words = BITS_TO_WORDS(curve->num_n_bits);
    wordcount_t i;
    uECC_word_t carry;
    uint8_t r_byte;

    if (!g_rng_function) {
        return 0;
    }
    if (!g_rng_function(&r_byte, COMB_RAND_BYTES)) {
        return 0;
    }
    uECC_vli_clear(r_padded, num_n_words);
    r_padded[0] = r_byte;

    uECC_vli_mult(product, r_padded, curve->n, num_n_words); /* product = r*n */

    uECC_vli_clear(k_blinded, COMB_SCALAR_WORDS);
    for (i = 0; i < COMB_SCALAR_WORDS; ++i) {
        k_blinded[i] = product[i];
    }
    carry = uECC_vli_add(k_blinded, k_blinded, k, num_n_words); /* += k, low num_n_words only */
    if (carry) {
        k_blinded[num_n_words] += 1; /* safe: word[num_n_words] < 256 since r <= 255 */
    }
    return 1;
}
